// Shared by the landing page and every documentation page. Everything here is an enhancement:
// each page reads correctly, unhighlighted and with its static navigation, when it never runs.
import hljs from "./vendor/highlight/core.min.js";
import csharp from "./vendor/highlight/languages/csharp.min.js";
import bash from "./vendor/highlight/languages/bash.min.js";
import json from "./vendor/highlight/languages/json.min.js";

hljs.registerLanguage("csharp", csharp);
hljs.registerLanguage("bash", bash);
hljs.registerLanguage("json", json);
hljs.registerAliases(["cs", "c#"], { languageName: "csharp" });
hljs.registerAliases(["sh", "shell", "console"], { languageName: "bash" });
// Only blocks that name a language: a plain-text block (console output) stays plain.
hljs.configure({ cssSelector: 'pre code[class*="lang"]' });
hljs.highlightAll();

// Copy buttons.
document.querySelectorAll("pre, .install").forEach(function (el) {
  var b = document.createElement("button");
  b.type = "button";
  b.className = "copy";
  b.innerHTML = '<span aria-live="polite">Copy</span>';
  b.addEventListener("click", function () {
    var label = b.firstChild;
    navigator.clipboard.writeText(el.querySelector("code").innerText).then(
      function () { label.textContent = "Copied"; },
      function () { label.textContent = "Copy failed"; }
    );
    b.dataset.done = "";
    setTimeout(function () { label.textContent = "Copy"; delete b.dataset.done; }, 1600);
  });
  el.appendChild(b);
});

// Documentation pages only: mark where the reader is, fill in the API tree, link the next guide.
var rail = document.querySelector(".rail");
if (rail) {
  // A sidebar on wide screens, a disclosure above the article on narrow ones.
  var toggle = rail.closest("details");
  var wide = matchMedia("(min-width: 901px)");
  var fit = function () { toggle.open = wide.matches; };
  fit();
  wide.addEventListener("change", fit);

  // The header says which half of the docs this is.
  var keep = document.querySelectorAll(".top nav a.keep");
  if (keep.length === 2) keep[/\/api\//.test(location.pathname) ? 1 : 0].setAttribute("aria-current", "true");

  var here = location.pathname.replace(/index\.html$/, "");
  var same = function (href) {
    return new URL(href, location.href).pathname.replace(/index\.html$/, "") === here;
  };
  var mark = function () {
    rail.querySelectorAll("a").forEach(function (a) {
      if (same(a.getAttribute("href"))) a.setAttribute("aria-current", "page");
    });
  };

  // The guide after this one, in the rail's own order.
  var guides = Array.prototype.slice.call(rail.querySelectorAll(".rail-guides a"));
  var at = guides.findIndex(function (a) { return same(a.getAttribute("href")); });
  var article = document.querySelector(".doc article");
  if (at > -1 && guides[at + 1] && article) {
    var next = document.createElement("a");
    next.className = "next";
    next.href = guides[at + 1].getAttribute("href");
    next.innerHTML = "<span>Next</span>" + guides[at + 1].innerHTML;
    article.appendChild(next);
  }

  // The API tree, grouped by the package each namespace ships in.
  var api = rail.querySelector(".rail-api");
  var tocUrl = api && api.dataset.toc;
  if (tocUrl) {
    fetch(tocUrl).then(function (r) { return r.ok ? r.json() : null; }).then(function (toc) {
      if (!toc) return;
      var base = new URL(tocUrl, location.href);
      var packages = [
        { name: "Atypical.X402.AspNetCore", test: /^X402\.AspNetCore/ },
        { name: "Atypical.X402.Client", test: /^X402\.Client/ },
        { name: "Atypical.X402.Core", test: /^X402/ },
      ];
      var html = "";
      packages.forEach(function (p) {
        var spaces = toc.items.filter(function (ns) {
          return p.test.test(ns.name) && packages.find(function (q) { return q.test.test(ns.name); }) === p;
        });
        if (!spaces.length) return;
        html += '<p class="rail-pkg">' + p.name + "</p><ul>";
        spaces.forEach(function (ns) {
          var open = (ns.items || []).some(function (t) { return same(new URL(t.href, base).href); }) ||
            same(new URL(ns.href, base).href);
          html += "<li><details" + (open ? " open" : "") + '><summary><a href="' +
            new URL(ns.href, base).href + '" title="' + ns.name + '">' +
            (ns.name.replace(/^X402\.(AspNetCore\.|Client\.)?/, "") || ns.name) + "</a></summary><ul>";
          (ns.items || []).forEach(function (t) {
            html += '<li><a href="' + new URL(t.href, base).href + '">' + t.name + "</a></li>";
          });
          html += "</ul></details></li>";
        });
        html += "</ul>";
      });
      api.insertAdjacentHTML("beforeend", html);
      mark();
      var current = api.querySelector('[aria-current="page"]');
      if (current) current.scrollIntoView({ block: "nearest" });
    });
  }
  mark();
}
