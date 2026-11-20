// From the repository root with Node 20+ and Google Chrome installed:
// npm install --prefix recursos --no-save --package-lock=false playwright@1.62.1
// node recursos/test_site_localization.cjs
const { chromium } = require("playwright");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const path = require("node:path");
const docs = path.resolve(__dirname, "../docs");

(async () => {
  const server = http.createServer((req, res) => {
    const file = path.resolve(docs, "." + (req.url === "/" ? "/index.html" : req.url.split("?")[0]));
    if (!file.startsWith(docs + path.sep) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
    const type = {".js":"text/javascript", ".html":"text/html", ".png":"image/png", ".svg":"image/svg+xml"}[path.extname(file)];
    res.setHeader("Content-Type", (type || "text/plain") + (type?.startsWith("text") ? "; charset=utf-8" : ""));
    res.end(fs.readFileSync(file));
  }).listen(0, "127.0.0.1");
  await new Promise(resolve => server.once("listening", resolve));
  let browser;
  try {
    browser = await chromium.launch({channel:"chrome", headless:true});
    const context = await browser.newContext({locale:"fr-FR"});
    const api = "https://api.github.com/repos/RichardKPL/multi-claude/releases";
    const latestAssets = "https://github.com/RichardKPL/multi-claude/releases/latest/download/";
    const binary = "https://github.com/RichardKPL/multi-claude/releases/download/v9.9.9/Multi-Claude-Portable.exe";
    const firstPage = api + "?per_page=100";
    const nextPage = api + "?per_page=100&page=2";
    const linkHeaders = {link: `<${nextPage}>; rel="next"`, "access-control-expose-headers":"Link"};
    const stable = {tag_name:"v9.9.9", assets:[
      {name:"helper.exe", size:2048, download_count:2, browser_download_url:"https://example.com/helper.exe"},
      {name:"Multi-Claude-Portable.exe", size:1048576, download_count:1000, browser_download_url:binary}
    ]};
    let deliverReleases;
    const waitForLanguage = new Promise(resolve => { deliverReleases = resolve; });
    await context.route(url => url.protocol === "https:", route => route.abort());
    await context.route(api + "/latest", async route => {
      await waitForLanguage;
      await route.fulfill({json:stable});
    });
    await context.route(url => url.href === firstPage, async route => {
      await waitForLanguage;
      await route.fulfill({headers:linkHeaders, json:[
        {tag_name:"v10-beta", prerelease:true, assets:[{name:"preview.exe", download_count:53}]},
        stable
      ]});
    });
    await context.route(url => url.href === nextPage, route => route.fulfill({json:[
      {tag_name:"v1", assets:[{name:"old.exe", download_count:179}, {name:"notes.txt", download_count:2500}]}
    ]}));
    const page = await context.newPage();
    page.setDefaultTimeout(5000);
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    const url = "http://127.0.0.1:" + server.address().port;
    await page.goto(url);
    assert.equal(await page.locator("#idioma").inputValue(), "fr");
    const catalog = await page.evaluate(() => window.siteMessages);
    const keys = Object.keys(catalog.en).sort();
    await page.setViewportSize({width:320,height:844});
    for (const [language, copy] of Object.entries(catalog)) {
      assert.deepEqual(Object.keys(copy).sort(), keys, language + " missing keys");
      assert(Object.values(copy).every(value => typeof value === "string" && value.trim()));
      await page.selectOption("#idioma", language);
      const failures = await page.evaluate(language => {
        const copy = window.siteMessages[language], failures = [];
        document.querySelectorAll("[data-i18n]").forEach(el => {
          if (el.textContent !== copy[el.dataset.i18n]) failures.push(el.dataset.i18n);
        });
        for (const [suffix, attr] of [["alt","alt"], ["aria","aria-label"], ["title","title"]]) {
          document.querySelectorAll("[data-i18n-" + suffix + "]").forEach(el => {
            if (el.getAttribute(attr) !== copy[el.getAttribute("data-i18n-" + suffix)]) failures.push(attr);
          });
        }
        if (!document.title.includes(copy.headline)) failures.push("document title");
        if (document.querySelector('meta[name="description"]').content !== copy.lead) failures.push("metadata");
        if (!document.getElementById("versao").textContent.includes(copy.portable)) failures.push("release");
        if (document.documentElement.scrollWidth > innerWidth) failures.push("mobile overflow");
        return failures;
      }, language);
      assert.deepEqual(failures, [], language);
      for (const image of await page.locator("[data-screenshot]").all()) {
        assert((await image.getAttribute("src")).includes("/" + language + "/"));
      }
    }
    await page.selectOption("#idioma", "ja");
    deliverReleases();
    await page.waitForFunction(() => document.getElementById("versao").textContent.includes("v9.9.9"));
    assert((await page.locator("#versao").textContent()).includes(catalog.ja.portable));
    assert((await page.locator("#versao").textContent()).includes("1 MB"));
    assert.equal(await page.locator("#baixar").getAttribute("href"), binary);
    assert.equal(await page.locator(".cta a").getAttribute("href"), binary);
    await page.waitForFunction(() => document.getElementById("downloads-count").textContent.includes("1,234"));
    assert.equal(await page.locator("#downloads-count").textContent(), "ダウンロード数: 1,234");
    await page.selectOption("#idioma", "de");
    assert((await page.locator("#versao").textContent()).includes(catalog.de.portable));
    assert.equal(await page.locator("#downloads-count").textContent(), "1.234 Downloads");
    await page.reload();
    assert.equal(await page.locator("#idioma").inputValue(), "de");
    // All prose must be explicitly translatable. Brands, language names and symbols are exempt.
    const unmarked = await page.evaluate(() => {
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      const result = [];
      let node;
      while ((node = walker.nextNode())) {
        const p = node.parentElement, text = node.textContent.trim();
        if (!text || p.closest("[data-i18n], script, option, #versao, #downloads-count, .ic")) continue;
        if (/^(Multi Claude(?: © 2026)?|Richard KPL|GitHub|GPL-3.0|Português, English,|[★⬇◐·]+$)/u.test(text)) continue;
        result.push(text);
      }
      return result;
    });
    assert.deepEqual(unmarked, []);
    await page.route("**/img/ko/*.png", route => route.abort());
    await page.selectOption("#idioma", "ko");
    await page.locator(".galeria").scrollIntoViewIfNeeded();
    await page.waitForFunction(() => [...document.querySelectorAll("[data-screenshot]")].every(img => img.src.includes("/img/en/") && img.complete && img.naturalWidth > 0));
    const fallback = await browser.newContext({locale:"ar-SA"});
    await fallback.route(url => url.protocol === "https:", route => route.abort());
    await fallback.addInitScript(() => {
      Object.defineProperty(window, "localStorage", {get() { throw new Error("disabled storage"); }});
    });
    const second = await fallback.newPage();
    second.setDefaultTimeout(5000);
    await second.goto(url);
    assert.equal(await second.locator("#idioma").inputValue(), "en");
    assert(await second.locator("#downloads-count").isHidden());
    assert.equal(await second.locator("#baixar").getAttribute("href"), latestAssets + "Multi-Claude-Portable.exe");
    assert.equal(await second.locator(".cta a").getAttribute("href"), latestAssets + "Multi-Claude-Portable.exe");
    await second.selectOption("#idioma", "pt");
    assert.equal(await second.locator("html").getAttribute("lang"), "pt-BR");
    // A later page can be rate-limited even when the latest release is available.
    await fallback.route(api + "/latest", route => route.fulfill({json:stable}));
    await fallback.route(url => url.href === firstPage, route => route.fulfill({json:[stable], headers:linkHeaders}));
    await fallback.route(url => url.href === nextPage, route => route.fulfill({status:403, json:{message:"rate limited"}}));
    const rateLimited = second.waitForResponse(url => url.url() === nextPage);
    await second.reload();
    await rateLimited;
    await second.waitForFunction(() => document.getElementById("versao").textContent.includes("v9.9.9"));
    assert(await second.locator("#downloads-count").isHidden());
    assert.equal(await second.locator(".cta a").getAttribute("href"), binary);
    // A stable release can omit the portable binary, for example while assets are uploading.
    const releasePage = "https://github.com/RichardKPL/multi-claude/releases/tag/v9.9.9";
    await fallback.route(api + "/latest", route => route.fulfill({
      json:{tag_name:"v9.9.9", html_url:releasePage, assets:[{name:"notes.txt"}]}
    }));
    await second.reload();
    await second.waitForFunction(() => document.getElementById("versao").textContent === "v9.9.9");
    assert.equal(await second.locator("#baixar").getAttribute("href"), releasePage);
    assert.equal(await second.locator(".cta a").getAttribute("href"), releasePage);
    const staticPage = await browser.newPage({javaScriptEnabled:false});
    await staticPage.route(url => url.protocol === "https:", route => route.abort());
    await staticPage.goto(url);
    const staticText = await staticPage.locator("[data-i18n]").evaluateAll(nodes =>
      nodes.map(node => [node.dataset.i18n, node.textContent])
    );
    for (const [key, value] of staticText) assert.equal(value, catalog.en[key], "static HTML: " + key);
    assert.deepEqual(errors, []);
    console.log("PASS: 10 catalogs, translated content and metadata, stable downloads, paginated totals, API failures, saved language, mobile layout, English fallbacks and disabled storage.");
  } finally {
    if (browser) await browser.close();
    server.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
