//using SoftCircuits.HtmlMonkey;
using System.Collections.Concurrent;
using System.Net;
using System.Xml.Linq;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WebShare.Generator
{
    internal class Program
    {
        class EndpointParamDoc
        {
            public string Name { get; set; }

            public string Type { get; set; }

            public string Description { get; set; }

            public bool IsOptional { get; set; }
        }

        class EndpointDoc
        {
            public string Name { get; set; }

            public string Description { get; set; }

            public List<EndpointParamDoc>? Params { get; set; }

            public string ReturnDesc { get; set; }

            public List<string> Requests { get; set; } = new List<string>();

            public List<string> Responses { get; set; } = new List<string>();

            public List<string> Errors { get; set; } = new List<string>();
        }

        readonly HttpClient client = new HttpClient();

        ConcurrentBag<EndpointDoc> ParsedEndpoints = new ConcurrentBag<EndpointDoc>();

        async Task<string> TryGetWebsiteContent(string url)
        {
            using HttpResponseMessage response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        Task ParseEndpoint(HtmlNode node)
        {
            EndpointDoc endpoint = new();

            var name = WebUtility.HtmlDecode(node.Attributes.First((att => att.Name == "id")).Value);
            var description = WebUtility.HtmlDecode(node.SelectSingleNode("p").InnerText);

            endpoint.Name = name;
            endpoint.Description = description;

            // params 
            var paramsList = node.SelectNodes(".//tr");
            var @params = new List<EndpointParamDoc>();

            if (paramsList != null)
            {
                foreach (var item in paramsList)
                {
                    var filteredParamNodes = item.ChildNodes.Where(x => x.NodeType != HtmlNodeType.Text).ToArray();

                    var inf = filteredParamNodes[0].InnerText;

                    if (inf == "@return")
                    {
                        endpoint.ReturnDesc = WebUtility.HtmlDecode(filteredParamNodes[1].InnerText);
                        break;
                    }

                    if (inf != "@param") continue;

                    var paramType = filteredParamNodes[1].InnerText;
                    var paramName = filteredParamNodes[2].InnerText;
                    var paramDescription = WebUtility.HtmlDecode(filteredParamNodes[3].InnerText);
                    var paramOptional = paramDescription.StartsWith("[optional]");

                    @params.Add(new()
                    {
                        Description = paramDescription.Replace("[optional] ", ""),
                        Name = paramName,
                        Type = paramType,
                        IsOptional = paramOptional,
                    });
                }
            }

            if (@params.Count > 0)
            {
                endpoint.Params = @params;
            }

            var requests = new List<string>();
            var responses = new List<string>();
            var errors = new List<string>();

            var filteredNodes = node.ChildNodes.Where(x => x.NodeType != HtmlNodeType.Text).ToArray();

            var reqTitle = filteredNodes[3].InnerText;

            if (reqTitle != "Request:")
            {
                throw new Exception("huh?");
            }

            // simple state machine to make things simple
            // and linear
            // 0 => reqs, 1 => resps, 2 => erros 
            int status = 0;
            foreach (var item in filteredNodes.Skip(3))
            {
                if (item.InnerText == "Request:")
                {
                    status = 0;
                }
                else if (item.InnerText == "Response:")
                {
                    status = 1;
                }
                else if (item.InnerText == "Errors:")
                {
                    status = 2;
                }

                if (item.OriginalName != "pre")
                    continue;

                switch (status)
                {
                    case 0: //reqs
                        {
                            //item
                            requests.Add(WebUtility.HtmlDecode(item.InnerText));
                            break;
                        }
                    case 1: //resps
                        {
                            responses.Add(WebUtility.HtmlDecode(item.InnerText));
                            break;
                        }
                    case 2: //errors
                        {
                            errors.Add(WebUtility.HtmlDecode(item.InnerText));
                            break;
                        }
                    default: break;
                }
            }

            endpoint.Requests.AddRange(requests);
            endpoint.Responses.AddRange(responses);
            endpoint.Errors.AddRange(errors);

            ParsedEndpoints.Add(endpoint);

            return Task.CompletedTask;
        }

        async Task Start()
        {
            // turns out the page loads all the doc at once and then just switches the visibility
            // makes things way easier tho in case of big docs, this can increase latency on the client

            var html = await TryGetWebsiteContent("https://webshare.cz/apidoc/");

            // main div path -> #function_details
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            var allEndpoints = htmlDoc.DocumentNode.SelectNodes("//*[@class=\"function_details\"]");

            //foreach (var node in allEndpoints)
            //{
            //    await ParseEndpoint(node);
            //}

            await Parallel.ForEachAsync(allEndpoints,
                new ParallelOptions() { MaxDegreeOfParallelism = 8 }, //change to 1 for debug
               async (node, token) =>
            {
                await ParseEndpoint(node);
            });

            var ser = JsonConvert.SerializeObject(ParsedEndpoints.OrderBy(x => x.Name));

            var path = Path.Combine(Environment.CurrentDirectory, "endpoints.json");

            if (File.Exists(path))
                File.Delete(path);

            await File.WriteAllTextAsync(path, ser.ToString());

            Console.WriteLine("ALL DONE");
        }

        // I dont wanna have everything static so ...
        // almost no TryCatch this time since its simple app
        static async Task Main(string[] args)
        {
            await new Program().Start();
        }
    }
}