using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

            arg.Response.StatusCode = 200;
            await arg.Response.Send("200 - Transcoding stopped");
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
                arg.Response.StatusCode = 404;
                await arg.Response.Send("404 - File not found");
            }
        }

        private async Task Server_DefaultRoute(HttpContext arg)
        {
            arg.Response.StatusCode = 200;
            await arg.Response.Send("Default route");
        }

        private async Task Server_Channel(HttpContext arg)
        {
            var channel = arg.Request.RawUrlEntries.LastOrDefault();
            if (channel == null)
            {
                arg.Response.StatusCode = 404;
                await arg.Response.Send("Not found");
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
                arg.Response.StatusCode = 500;
                await arg.Response.Send("500 - Error transcoding stream");
                return;
            }

            _logger.LogDebug("Redirecting to transcoded M3U8 file");

            arg.Response.StatusCode = 302;
            arg.Response.Headers.Add("Location", $"{_config.UrlPrefix}:{_config.HttpPort}/stream/{channel}.m3u8");
            await arg.Response.Send();

            _lastAccess = DateTime.Now;
            _tmrAutoStop.Start();
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

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _httpServer = new Server("127.0.0.1", _config.HttpPort, false, Server_DefaultRoute)
            {
                AccessControl =
                {
                    Mode = AccessControlMode.DefaultPermit
                }
            };
            _httpServer.DynamicRoutes.Add(HttpMethod.GET, new Regex("^/channel/\\d+$"), Server_Channel);
            _httpServer.DynamicRoutes.Add(HttpMethod.GET, new Regex("^/stream/*.*$"), Server_Stream);
            _httpServer.StaticRoutes.Add(HttpMethod.GET, "/stop", Server_Stop);

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
