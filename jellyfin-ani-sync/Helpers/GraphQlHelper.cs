using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace jellyfin_ani_sync.Helpers
{
    public class GraphQlHelper
    {
        public static async Task<HttpResponseMessage> Request(HttpClient httpClient, string query, Dictionary<string, object> variables = null)
        {
            var call = await httpClient.PostAsync("https://graphql.anilist.co", new StringContent(JsonSerializer.Serialize(new GraphQl { Query = query, Variables = variables }), Encoding.UTF8, "application/json"));

            return call.IsSuccessStatusCode ? call : null;
        }

        public static async Task<T> DeserializeRequest<T>(HttpClient httpClient, string query, Dictionary<string, object> variables)
        {
            var response = await GraphQlHelper.Request(httpClient, query, variables);
            if (response != null)
            {
                StreamReader streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
                return JsonSerializer.Deserialize<T>(await streamReader.ReadToEndAsync());
            }

            return default;
        }

        private class GraphQl
        {
            [JsonPropertyName("query")] public string Query { get; set; }
            [JsonPropertyName("variables")] public Dictionary<string, object> Variables { get; set; }
        }
    }
}