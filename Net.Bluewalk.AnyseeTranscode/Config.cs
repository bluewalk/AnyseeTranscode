using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualBasic.CompilerServices;
using Net.Bluewalk.DotNetEnvironmentExtensions;

namespace Net.Bluewalk.AnyseeTranscode
{
    public class Config
    {
        [EnvironmentVariable(Name = "ANYSEE_URL", Default = "http://localhost/chlist/")]
        public string AnyseeUrl { get; set; }

        [EnvironmentVariable(Name = "HTTP_PORT", Default = 8080)]
        public int HttpPort { get; set; }

        [EnvironmentVariable(Name = "URL_PREFIX", Default = "http://localhost")]
        public string UrlPrefix { get; set; }

        [EnvironmentVariable(Name = "FFMPEG_EXE",  Default = "ffmpeg")]
        public string FfmpegExe { get; set; }

        [EnvironmentVariable(Name = "SEGMENT_PATH", Default = "/tmp")]
        public string SegmentPath { get; set; }
    }
}
