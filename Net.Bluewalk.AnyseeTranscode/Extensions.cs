using System.Threading.Tasks;
using Newtonsoft.Json;
using WatsonWebserver;

namespace Net.Bluewalk.AnyseeTranscode
{
    public static class Extensions
    {
        public static async Task SendJson(this HttpResponse response, object value, int status = 200)
        {
            response.StatusCode = 200;
            response.ContentType = "application/json";
            await response.Send(JsonConvert.SerializeObject(value));
        }

        public static async Task Redirect(this HttpResponse response, string uri)
        {
            response.StatusCode = 302;
            response.Headers.Add("Location", uri);
            await response.Send();
        }

        public static async Task NotFound(this HttpResponse response)
        {
            await response.Error(404, "Not found");
        }

        public static async Task Error(this HttpResponse response, int status = 400, string message = null)
        {
            response.StatusCode = status;
            await response.Send(JsonConvert.SerializeObject(new
            {
                Code = status,
                Message = message
            }));
        }
    }
}
