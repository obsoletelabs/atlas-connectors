using System;
using Atlas.Client;

namespace Atlas.Client.SourceCheck
{
    /// <summary>What a connector does: pick its serializer adapter and build a client.</summary>
    public static class Consumer
    {
        public static AtlasClient Create(string baseUrl, Func<string> token)
        {
            var options = new AtlasClientOptions { BaseUri = new Uri(baseUrl), TokenProvider = token, HostName = "playnite", HostVersion = "0.0.0" };
            return new AtlasClient(options, new NewtonsoftJsonAdapter());
        }
    }
}
