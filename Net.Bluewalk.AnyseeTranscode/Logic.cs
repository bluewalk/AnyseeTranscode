using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Net.Bluewalk.DotNetEnvironmentExtensions;

namespace Net.Bluewalk.AnyseeTranscode
{
    public class Logic : IHostedService
    {
        private ILogger<Logic> _logger;
        private HttpListener httpServer;
        private Process _transcodingProc;
        private DateTime lastAccess = DateTime.Now;
        private System.Timers.Timer _tmrAutoStop;
        private readonly Config _config;
        private string _currChannel;

        public Logic(ILogger<Logic> logger)
        {
            _logger = logger;

            _config = (Config)typeof(Config).FromEnvironment();

            httpServer = new HttpListener();
            httpServer.Prefixes.Add($"http://*:{_config.HttpPort}/");
            httpServer.Start();

            _tmrAutoStop = new System.Timers.Timer(15000);
            _tmrAutoStop.Elapsed += (sender, args) =>
            {
                if (lastAccess < DateTime.Now.AddMinutes(-1) && _transcodingProc != null)
                {
                    KillTranscoding();
                }
            };
        }


        private void Cleanup()
        {
            foreach (var f in new DirectoryInfo(_config.SegmentPath).GetFiles(string.Format("*.*")))
            {
                try
                {

                    f.Delete();
                }
                catch { }
            }
        }

        private void KillTranscoding()
        {
            _tmrAutoStop.Stop();

            if (_transcodingProc != null)
            {
                try
                {
                    _logger.LogDebug("Trying to kill the FFMPEG process");
                    _transcodingProc.Kill();
                    Thread.Sleep(2000); // Wait two seconds for the Anysee to release the channel stream
                }
                catch { }
                finally
                {
                    _transcodingProc = null;

                    Cleanup();
                }
            }
        }


        private void RunProcess(string parameters)
        {
            var oInfo = new ProcessStartInfo(_config.FfmpegExe, parameters)
            {
                WorkingDirectory = _config.SegmentPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            try
            {
                _logger.LogInformation("Starting FFMPEG with parameters: {0}", parameters);

                _transcodingProc = Process.Start(oInfo);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error during starting FFMPEG process");
            }
        }


        private void Transcode(string channel)
        {
            KillTranscoding();

            _logger.LogInformation("Start transcoding channel {0}", channel);

            //            RunProcess(String.Format("-i {0}{1} -async 1 -ss 00:00:05 -acodec libmp3lame -ac 2 -vcodec libx264 -preset superfast -tune zerolatency -threads 2 -s 720x408 -flags -global_header -fflags +genpts -map 0:0 -map 0:1 -hls_time 10 -hls_wrap 15 {1}.m3u8 -segment_list_flags +live -segment_time 10",
            //            RunProcess(String.Format("-i {0}{1} -async 1 -ss 00:00:05 -threads 0 -acodec aac -strict -2 -cutoff 15000 -ac 2 -ab 256k -vcodec libx264 -preset superfast -tune zerolatency -threads 2 -flags -global_header -fflags +genpts -map 0:0 -map 0:1 -hls_time 10 -hls_wrap 15 {1}.m3u8 -segment_format mpegts -segment_list_flags +live -segment_time 10",
            RunProcess(string.Format("-i {0}{1} -async 1 -threads 0 -acodec aac -strict -2 -cutoff 15000 -ac 2 -ab 256k -vcodec libx264 -preset ultrafast -tune zerolatency -threads 2 -flags -global_header -fflags +genpts -map 0:0 -map 0:1 -hls_time 5 -hls_wrap 12 {1}.m3u8 -segment_format mpegts -segment_list_flags +live -segment_time 10",
                _config.AnyseeUrl, channel));
        }

        private void StartListening(CancellationToken cancellationToken)
        {
            try
            {
                while (httpServer.IsListening && !cancellationToken.IsCancellationRequested)
                {
                    ThreadPool.QueueUserWorkItem((c) =>
                    {
                        HttpListenerResponse response = null;

                        try
                        {
                            var context = c as HttpListenerContext;

                            // Select channel to transcode
                            if (context.Request.Url.AbsolutePath.ToUpper().Contains("/CHANNEL"))
                            {
                                var channel = Regex.Match(context.Request.Url.AbsolutePath, @"\d+").Value;

                                if (!channel.Equals(_currChannel) || _transcodingProc == null)
                                {
                                    _currChannel = channel;
                                    Transcode(channel);
                                }

                                while (!File.Exists(Path.Combine(_config.SegmentPath, channel + ".m3u8")))
                                {
                                    Thread.Sleep(1000);
                                }

                                _logger.LogDebug("Redirecting to transcoded M3U8 file");

                                response = context.Response;
                                response.Redirect($"{_config.UrlPrefix}/streams/{channel}.m3u8");

                                lastAccess = DateTime.Now;
                                _tmrAutoStop.Start();
                            }

                            // Request a file
                            else if (context.Request.Url.AbsolutePath.ToUpper().Contains("/STREAMS"))
                            {
                                var requestFile = Path.Combine(_config.SegmentPath,
                                    Path.GetFileName(context.Request.Url.AbsolutePath));

                                response = context.Response;

                                if (File.Exists(requestFile))
                                {
                                    lastAccess = DateTime.Now;
                                    _logger.LogDebug("Serving {0}", requestFile);

                                    switch (Path.GetExtension(requestFile).ToUpper())
                                    {
                                        case ".M3U8":
                                            response.ContentType = "application/x-mpegURL";
                                            break;
                                        case ".TS":
                                            response.ContentType = "video/mp2t";
                                            break;
                                        default:
                                            response.ContentType = "application/octet-stream";
                                            break;
                                    }

                                    try
                                    {
                                        var data = File.ReadAllBytes(requestFile);

                                        response.SendChunked = true;
                                        response.ContentLength64 = data.Length;

                                        using var s = response.OutputStream;
                                        s.Write(data, 0, data.Length);
                                    }
                                    catch (Exception e)
                                    {
                                        _logger.LogError(e, $"An error occurred accessing {requestFile}");
                                    }
                                }
                                else
                                {
                                    response.StatusCode = 404;

                                    var buf = Encoding.UTF8.GetBytes("404 - File not found");
                                    response.OutputStream.Write(buf, 0, buf.Length);
                                }
                            }
                            
                            // Select channel to transcode
                            else if (context.Request.Url.AbsolutePath.ToUpper().Contains("/STOP"))
                            {
                                KillTranscoding();

                                response = context.Response;

                                var buf = Encoding.UTF8.GetBytes("200 - Transcoding stopped");
                                response.OutputStream.Write(buf, 0, buf.Length);
                            }

                            // Default response
                            else
                            {
                                response = context.Response;

                                response.StatusCode = 400;

                                var buf = Encoding.UTF8.GetBytes("400 - Bad request");
                                response.OutputStream.Write(buf, 0, buf.Length);
                            }
                        }
                        finally
                        {
                            if (response != null)
                            {
                                response.OutputStream.Flush();
                                response.OutputStream.Close();
                                response.Close();
                            }
                        }
                    }, httpServer.GetContext());
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error occurred during serving HTTP");
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            ThreadPool.QueueUserWorkItem((o) =>
            {
                StartListening(cancellationToken);
            });
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_transcodingProc != null)
            {
                KillTranscoding();
            }

            return Task.FromResult(true);
        }
    }
}
