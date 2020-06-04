using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            var requestFile = Path.Combine(_config.SegmentPath,
                Path.GetFileName(arg.Request.RawUrlWithoutQuery));

            if (File.Exists(requestFile))
            {
                _lastAccess = DateTime.Now;
                _logger.LogDebug("Serving {0}", requestFile);

                switch (Path.GetExtension(requestFile).ToUpper())
                {
                    case ".M3U8":
                        arg.Response.ContentType = "application/x-mpegURL";
                        break;
                    case ".TS":
                        arg.Response.ContentType = "video/mp2t";
                        break;
                    default:
                        arg.Response.ContentType = "application/octet-stream";
                        break;
                }

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
            var channel = arg.Request.RawUrlEntries.LastOrDefault();
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

            while (!File.Exists(Path.Combine(_config.SegmentPath, channel + ".m3u8")) && !_transcodingProcess.HasExited)
                Thread.Sleep(1000);

            if (_transcodingProcess.HasExited)
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
            var result = new List<object>();

            var m3u = await GetChannelM3U();
            m3u.Medias.ToList().ForEach(m =>
            {
                var title = Regex.Match(m.Title.InnerTitle, @"(?<channel>[0-9]+)\(cab\)\.(?<name>[A-Za-z0-9_]+)");
                var channelId = m.MediaFile.Split('/').Last();

                result.Add(new
                {
                    Channel = Convert.ToInt32(title.Groups["channel"].Value ?? "-1"),
                    Name = title.Groups["name"].Value?.Replace("_", " ").Trim(),
                    Id = channelId,
                    Encrypted = m.Title.InnerTitle.EndsWith("_$"),
                    DirectUrl = $"http://{_config.AnyseeIp}:8080/chlist/{channelId}",
                    TranscodeUrl = $"{_config.UrlPrefix}/channel/{channelId}"
                });
            });

            await arg.Response.SendJson(result);
        }

        private async Task Server_Stats(HttpContext arg)
        {
            await arg.Response.SendJson(_httpServer.Stats);
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
                catch { }
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

            _logger.LogInformation("Start transcoding channel {0}", channel);

            try
            {
                var parameters = string.Format(
                    "-i {0}{1} -async 1 -threads 0 -acodec aac -strict -2 -cutoff 15000 -ac 2 -ab 256k -vcodec libx264 -preset ultrafast -tune zerolatency -threads 2 -flags -global_header -fflags +genpts -map 0:0 -map 0:1 -hls_time 5 -hls_wrap 12 {1}.m3u8 -segment_format mpegts -segment_list_flags +live -segment_time 10",
                    $"http://{_config.AnyseeIp}:8080/chlist/", channel);

                var oInfo = new ProcessStartInfo(_config.FfmpegExe, parameters)
                {
                    WorkingDirectory = _config.SegmentPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };

                _logger.LogInformation("Starting FFMPEG");
                _logger.LogDebug($"{oInfo.FileName} {oInfo.Arguments}");

                _transcodingProcess = Process.Start(oInfo);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error starting FFMPEG process");
            }
        }
        #endregion

        public async Task<Extm3u> GetChannelM3U(bool reload = false)
        {
            return _m3u ??= await M3U.ParseFromUrlAsync($"http://{_config.AnyseeIp}/n7_tv_chlist.m3u");
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting webserver");
            _httpServer = new Server("+", _config.HttpPort, false,
                Server_DefaultRoute)
            {
                AccessControl =
                {
                    Mode = AccessControlMode.DefaultPermit
                }
            };
            _httpServer.DynamicRoutes.Add(HttpMethod.GET, new Regex("^/channel/\\d+$"), Server_Channel);
            _httpServer.DynamicRoutes.Add(HttpMethod.GET, new Regex("^/stream/*.*$"), Server_Stream);
            _httpServer.StaticRoutes.Add(HttpMethod.GET, "/stop", Server_Stop);
            _httpServer.StaticRoutes.Add(HttpMethod.GET, "/channels", Server_Channels);
            _httpServer.StaticRoutes.Add(HttpMethod.GET, "/stats", Server_Stats);
            _logger.LogInformation("Webserver running at port {0}", _config.HttpPort);

            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_transcodingProcess != null)
                KillTranscoding();

            _httpServer.Dispose();

            return Task.FromResult(true);
        }
    }
}
