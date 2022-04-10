using Net.Bluewalk.DotNetEnvironmentExtensions;

namespace Net.Bluewalk.AnyseeTranscode
{
    public class Config
    {
        [EnvironmentVariable(Name = "ANYSEE_IP", Default = "192.168.1.30")]
        public string AnyseeIp { get; set; }

        [EnvironmentVariable(Name = "URL_PREFIX", Default = "http://localhost:8080")]
        public string UrlPrefix { get; set; }

        [EnvironmentVariable(Name = "FFMPEG_EXE",  Default = "ffmpeg")]
        public string FfmpegExe { get; set; }

        [EnvironmentVariable(Name = "SEGMENT_PATH", Default = "/tmp")]
        public string SegmentPath { get; set; }
    }
}
