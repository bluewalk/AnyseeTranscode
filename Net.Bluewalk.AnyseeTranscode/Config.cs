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

        [EnvironmentVariable(Name ="FFMPEG_PARAMS", Default = "-async 1 -threads 0 -acodec aac -strict -2 -cutoff 15000 -ac 2 -ab 256k -vcodec libx264 -preset ultrafast -tune zerolatency -threads 2 -flags -global_header -fflags +genpts -map 0:0 -map 0:1 -hls_time 5 -hls_wrap 12 [CHANNEL].m3u8 -segment_format mpegts -segment_list_flags +live -segment_time 10")]
        public string FfmpegParams { get; set; }

        [EnvironmentVariable(Name = "SEGMENT_PATH", Default = "/tmp")]
        public string SegmentPath { get; set; }
    }
}
