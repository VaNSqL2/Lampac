﻿using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Lampac.Engine.CORE;
using Shared.Engine.CORE;
using Online;
using Shared.Model.Templates;
using Shared.Model.Online;
using System.Web;
using Lampac.Models.LITE;
using Shared.Model.Online.Vibix;
using System.Collections.Generic;
using System.Linq;

namespace Lampac.Controllers.LITE
{
    public class Vibix : BaseOnlineController
    {
        ProxyManager proxyManager = new ProxyManager(AppInit.conf.Vibix);

        [HttpGet]
        [Route("lite/vibix")]
        async public ValueTask<ActionResult> Index(string imdb_id, long kinopoisk_id, string title, string original_title,  int s = -1, bool rjson = false, bool origsource = false)
        {
            var init = await loadKit(AppInit.conf.Vibix);
            if (await IsBadInitialization(init, rch: true))
                return badInitMsg;

            if (string.IsNullOrEmpty(init.token))
                return OnError();

            JObject data = await search(init, imdb_id, kinopoisk_id);
            if (data == null)
                return OnError();

            reset: var proxy = proxyManager.Get();
            var rch = new RchClient(HttpContext, host, init, requestInfo);
            if (rch.IsNotSupport("web", out string rch_error))
                return ShowError(rch_error);

            string iframe_url = data.Value<string>("iframe_url");
            var cache = await InvokeCache<EmbedModel>(rch.ipkey($"vibix:iframe:{iframe_url}:{init.token}", proxyManager), cacheTime(20, rhub: 2, init: init), rch.enable ? null : proxyManager, async res =>
            {
                if (rch.IsNotConnected())
                    return res.Fail(rch.connectionMsg);

                string html = rch.enable ? await rch.Get(init.cors(iframe_url), httpHeaders(init)) :
                                           await HttpClient.Get(init.cors(iframe_url), timeoutSeconds: 8, proxy: proxy, headers: httpHeaders(init));

                if (html == null)
                    return res.Fail("html");

                string file = null;

                if (data.Value<string>("type") == "movie")
                {
                    file = html.Split(",file:")?[1]?.Split("function")?[0];
                    if (string.IsNullOrEmpty(file) || !file.Contains("/get_file/"))
                        return res.Fail("file");

                    return new EmbedModel() { content = file };
                }
                else
                {
                    string json = Regex.Match(html, "new Playerjs\\(([^\n\r]+)").Groups[1].Value.Split(");")[0];

                    var jObj = JObject.Parse(json);
                    var fileToken = jObj["file"];
                    if (fileToken == null)
                        return res.Fail("file");

                    try
                    {
                        return new EmbedModel() { serial = fileToken.ToObject<List<Seasons>>() };
                    }
                    catch { return res.Fail("DeserializeObject"); }
                }
            });

            if (IsRhubFallback(cache, init))
                goto reset;

            if (data.Value<string>("type") == "movie")
            {
                #region Фильм
                return OnResult(cache, () => 
                {
                    var mtpl = new MovieTpl(title, original_title, 1);

                    var streams = new StreamQualityTpl();

                    foreach (string q in new string[] { "1080p", "720p", "480p" })
                    {
                        var g = new Regex($"({q})\\](https?://[^,\t ]+\\.mp4)").Match(cache.Value.content).Groups;

                        if (!string.IsNullOrEmpty(g[2].Value))
                            streams.Append(HostStreamProxy(init, g[2].Value, proxy: proxy), g[1].Value);
                    }

                    if (streams.Any())
                        mtpl.Append("По умолчанию", streams.Firts().link, streamquality: streams, vast: init.vast);

                    return rjson ? mtpl.ToJson(reverse: true) : mtpl.ToHtml(reverse: true);

                }, origsource: origsource, gbcache: !rch.enable);
                #endregion
            }
            else
            {
                #region Сериал
                return OnResult(cache, () =>
                {
                    string enc_title = HttpUtility.UrlEncode(title);
                    string enc_original_title = HttpUtility.UrlEncode(original_title);
                    
                    if (s == -1)
                    {
                        var tpl = new SeasonTpl(cache.Value.serial.Count);

                        foreach (var season in cache.Value.serial)
                        {
                            string name = season.title;
                            if (int.TryParse(Regex.Match(name, "([0-9]+)$").Groups[1].Value, out int _s) && _s > 0)
                            {
                                string link = $"{host}/lite/vibix?rjson={rjson}&kinopoisk_id={kinopoisk_id}&imdb_id={imdb_id}&title={enc_title}&original_title={enc_original_title}&s={_s}";
                                tpl.Append($"{_s} сезон", link, _s);
                            }
                        }

                        return rjson ? tpl.ToJson() : tpl.ToHtml();
                    }
                    else
                    {
                        var etpl = new EpisodeTpl();
                        string sArhc = s.ToString();

                        foreach (var season in cache.Value.serial)
                        {
                            if (!season.title.EndsWith($" {s}"))
                                continue;

                            foreach (var episode in season.folder)
                            {
                                string name = episode.title;
                                string file = episode?.folder?.First()?.file ?? episode.file;

                                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file))
                                    continue;

                                var streams = new StreamQualityTpl();

                                foreach (string q in new string[] { "1080p", "720p", "480p" })
                                {
                                    var g = new Regex($"({q})\\](\\{{[^\\}}]+\\}})?(?<file>https?://[^,\t\\[\\;\\{{ ]+\\.mp4)").Match(file).Groups;
                                    if (!string.IsNullOrEmpty(g["file"].Value))
                                        streams.Append(HostStreamProxy(init, g["file"].Value, proxy: proxy), g[1].Value);
                                }

                                if (streams.Any())
                                    etpl.Append(name, title ?? original_title, sArhc, Regex.Match(name, "([0-9]+)").Groups[1].Value, streams.Firts().link, streamquality: streams, vast: init.vast);
                            }
                        }

                        return rjson ? etpl.ToJson() : etpl.ToHtml();
                    }

                }, origsource: origsource, gbcache: !rch.enable);
                #endregion
            }
        }


        #region search
        async ValueTask<JObject> search(OnlinesSettings init, string imdb_id, long kinopoisk_id)
        {
            string memKey = $"vibix:view:{kinopoisk_id}:{imdb_id}";

            if (!hybridCache.TryGetValue(memKey, out JObject root))
            {
                async Task<JObject> goSearch(string imdb_id, long kinopoisk_id)
                {
                    if (string.IsNullOrEmpty(imdb_id) && kinopoisk_id == 0)
                        return null;

                    string uri = kinopoisk_id > 0 ? $"kp/{kinopoisk_id}" : $"imdb/{imdb_id}";
                    var header = httpHeaders(init, HeadersModel.Init(
                        ("Accept", "application/json"),
                        ("Authorization", $"Bearer {init.token}"),
                        ("X-CSRF-TOKEN", "")
                    ));

                    var video = await HttpClient.Get<JObject>($"{init.host}/api/v1/publisher/videos/{uri}", timeoutSeconds: 8, proxy: proxyManager.Get(), headers: header);

                    if (video == null)
                    {
                        proxyManager.Refresh();
                        return null;
                    }

                    if (!video.ContainsKey("id"))
                        return null;

                    return video;
                }

                root = await goSearch(null, kinopoisk_id) ?? await goSearch(imdb_id, 0);
                if (root == null)
                    return null;

                proxyManager.Success();
                hybridCache.Set(memKey, root, cacheTime(30, init: init));
            }

            return root;
        }
        #endregion
    }
}
