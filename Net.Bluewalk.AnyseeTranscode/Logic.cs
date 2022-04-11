using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using m3uParser;
using m3uParser.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Net.Bluewalk.DotNetEnvironmentExtensions;
using WatsonWebserver;

namespace Net.Bluewalk.AnyseeTranscode
{
    public class Logic : IHostedService
    {
        private const string ContentTypeJson = "application/json";
        private const string ContentTypePlaylist = "application/x-mpegURL";
        private const string ContentTypeVideo = "video/mp2t";
        private const string ContentTypeOctetStream = "application/octet-stream";

        private readonly ILogger<Logic> _logger;
        private Server _httpServer;
        private Process _transcodingProcess;
        private DateTime _lastAccess = DateTime.Now;
        private readonly System.Timers.Timer _tmrAutoStop;
        private readonly Config _config;
        private string _currChannel;
        private Extm3u _m3u;

        public Logic(ILogger<Logic> logger)
        {
            _logger = logger;

            _config = (Config)typeof(Config).FromEnvironment();

            _tmrAutoStop = new System.Timers.Timer(15000);
            _tmrAutoStop.Elapsed += (sender, args) =>
            {
                if (_lastAccess < DateTime.Now.AddMinutes(-1) && _transcodingProcess != null)
                    KillTranscoding();
            };
        }

        #region Webserver calls
        private async Task Server_Stop(HttpContext arg)
        {
            KillTranscoding();

            await arg.Response.SendJson(new
            {
                Stopped = true
            });
        }

        private async Task Server_Stream(HttpContext arg)
        {
            //    var requestFile = Path.Combine(_config.SegmentPath,
            //        Path.GetFileName(arg.Request.RawUrlWithoutQuery));

            var requestFile = Path.Combine(_config.SegmentPath, Path.GetFileName(arg.Request.Url.RawWithoutQuery));

            if (File.Exists(requestFile))
            {
                _lastAccess = DateTime.Now;
                _logger.LogDebug("Serving {0}", requestFile);

                arg.Response.ContentType = Path.GetExtension(requestFile).ToUpper() switch
                {
                    ".M3U8" => ContentTypePlaylist,
                    ".TS" => ContentTypeVideo,
                    _ => ContentTypeOctetStream
                };

                try
                {
                    var data = await File.ReadAllBytesAsync(requestFile);

                    arg.Response.StatusCode = 200;
                    await arg.Response.Send(data);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"An error occurred accessing {requestFile}");
                }
            }
            else
            {
                await arg.Response.NotFound();
            }
        }

        private async Task Server_DefaultRoute(HttpContext arg)
        {
            await arg.Response.SendJson("Default route");
        }

        private async Task Server_Channel(HttpContext arg)
        {
            var channel = arg.Request.Url.Elements.LastOrDefault();
            if (channel == null)
            {
                await arg.Response.NotFound();
                return;
            }

            if (!channel.Equals(_currChannel) || _transcodingProcess == null || _transcodingProcess.HasExited)
            {
                _currChannel = channel;
                Transcode(channel);
            }

            while (!File.Exists(Path.Combine(_config.SegmentPath, channel + ".m3u8")) && _transcodingProcess?.HasExited == false)
                Thread.Sleep(1000);

            if (_transcodingProcess?.HasExited == true)
            {
                await arg.Response.Error(500, "Error transcoding stream");
                return;
            }

            _logger.LogDebug("Redirecting to transcoded M3U8 file");

            await arg.Response.Redirect($"{_config.UrlPrefix}/stream/{channel}.m3u8");

            _lastAccess = DateTime.Now;
            _tmrAutoStop.Start();
        }

        private async Task Server_Channels(HttpContext arg)
        {
            try
            {
                var result = new M3UPlaylist();

                var m3u = await GetChannelM3U();
                m3u.Medias.ToList().ForEach(m =>
                {
                    var titleRegexMatches = Regex.Match(m.Title.InnerTitle, @"(?<channel>[0-9]+)\(cab\)\.(?<name>[A-Za-z0-9_]+)");
                    var title = titleRegexMatches.Groups["name"].Value?.Replace("_", " ").Trim();
                    var channelNumber = titleRegexMatches.Groups["channel"].Value;
                    var channelId = m.MediaFile.Split('/').Last();

                    result.Add(new M3UPlaylistEntry()
                    {
                        Title = $"{channelNumber}_{title}",
                        Url = new Uri($"{_config.UrlPrefix}/channel/{channelId}"),
                        TvgName = title

                        //Channel = Convert.ToInt32(title.Groups["channel"].Value ?? "-1"),
                        //Name = title.Groups["name"].Value?.Replace("_", " ").Trim(),
                        //Id = channelId,
                        //Encrypted = m.Title.InnerTitle.EndsWith("_$"),
                        //DirectUrl = $"http://{_config.AnyseeIp}:8080/chlist/{channelId}",
                        //TranscodeUrl = $"{_config.UrlPrefix}/channel/{channelId}"
                    });
                });

                if (arg.Request.HeaderExists("Accept", false) && arg.Request.Headers["Accept"] == ContentTypeJson)
                    await arg.Response.SendJson(result);
                else
                {
                    arg.Response.ContentType = ContentTypePlaylist;
                    arg.Response.Headers["Content-Disposition"] = "attachment; filename=playlist.m3u8";

                    await arg.Response.Send(Encoding.UTF8.GetBytes(result?.ToString()));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating channel playlist");
            }
        }

        private async Task Server_Status(HttpContext arg)
        {
            await arg.Response.SendJson(_httpServer.Statistics);
        }

        #endregion

        #region Transcoding logic
        private void Cleanup()
        {
            _logger.LogInformation("Cleaning up old stream data");

            foreach (var f in new DirectoryInfo(_config.SegmentPath).GetFiles(string.Format("*.*")))
            {
                try
                {
                    _logger.LogDebug("Deleting {0}", f.Name);

                    f.Delete();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error cleaning up {file}", f);
                }
            }
        }

        private void KillTranscoding()
        {
            _tmrAutoStop.Stop();

            if (_transcodingProcess == null) return;

            try
            {
                _logger.LogInformation("Stop transcoding");

                _logger.LogDebug("Trying to kill the FFMPEG process");
                _transcodingProcess.Kill();
                _transcodingProcess = null;

                Thread.Sleep(2000); // Wait two seconds for the Anysee to release the channel stream
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when killing FFPMPEG");
            }
            finally
            {
                _transcodingProcess = null;
                _logger.LogInformation("Transcoding stopped");

                Cleanup();
            }
        }


        private void Transcode(string channel)
        {
            KillTranscoding();

            _logger.LogInformation("Start transcoding channel {channel}", channel);

            try
            {
                var parameters = $"-i http://{_config.AnyseeIp}:8080/chlist/{channel} {_config.FfmpegParams.Replace("[CHANNEL]", channel)}";

                var sInfo = new ProcessStartInfo(_config.FfmpegExe, parameters)
                {
                    WorkingDirectory = _config.SegmentPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true
                };

                _transcodingProcess = new Process()
                {
                    StartInfo = sInfo,
                    EnableRaisingEvents = true
                };

                _transcodingProcess.ErrorDataReceived += (sender, args) => _logger.LogDebug($"FFMPEG: {args.Data}");

                _logger.LogInformation("Starting FFMPEG");
                _logger.LogDebug($"{sInfo.FileName} {sInfo.Arguments}");

                if (_transcodingProcess.Start())
                {
                    _logger.LogInformation("FFMPEG started, transcoding");
                    _transcodingProcess.BeginErrorReadLine();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error starting FFMPEG process");
            }
        }
        #endregion

        public async Task<Extm3u> GetChannelM3U()
        {
            return _m3u ??= await M3U.ParseFromUrlAsync($"http://{_config.AnyseeIp}/n7_tv_chlist.m3u");
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting webserver");
            _httpServer = new Server("+", 80, false, Server_DefaultRoute);
            _httpServer.Settings.AccessControl.Mode = AccessControlMode.DefaultPermit;

            _httpServer.Routes.Dynamic.Add(HttpMethod.GET, new Regex("^/channel/\\d+$"), Server_Channel);
            _httpServer.Routes.Dynamic.Add(HttpMethod.GET, new Regex("^/stream/*.*$"), Server_Stream);
            _httpServer.Routes.Static.Add(HttpMethod.GET, "/stop", Server_Stop);
            _httpServer.Routes.Static.Add(HttpMethod.GET, "/channels", Server_Channels);
            _httpServer.Routes.Static.Add(HttpMethod.GET, "/status", Server_Status);

            _httpServer.Start();

            _logger.LogInformation("Webserver running at port {0}", 80);

            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _httpServer.Stop();

            if (_transcodingProcess != null)
                KillTranscoding();

            _httpServer.Dispose();

            return Task.FromResult(true);
        }
    }
}
