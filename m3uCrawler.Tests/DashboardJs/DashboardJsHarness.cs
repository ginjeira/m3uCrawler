using System;
using System.Text.Json;
using Jint;
using Jint.Native;

namespace m3uCrawler.Tests.DashboardJs;

/// <summary>
/// DC-8 (enabler) — extrai o bloco <c>&lt;script&gt;</c> (IIFE) servido em
/// <c>GET /</c> pelo Dashboard e corre-o num motor JavaScript gerido
/// (<see href="https://github.com/sebastienros/jint">Jint</see>, puro .NET,
/// BSD-2-Clause) com um shim DOM mínimo, 100% in-process e determinístico.
///
/// Não edita <c>WebDashboardService.cs</c>; a fonte é sempre o HTML servido
/// em runtime, pelo que o harness não depende de números de linha.
/// </summary>
internal static class DashboardJsScript
{
    /// <summary>
    /// Marcador único do IIFE que contém os handlers do Live Run. Usado para
    /// localizar o bloco <c>&lt;script&gt;</c> correcto entre os vários do
    /// documento (o bootstrap tem o seu próprio script).
    /// </summary>
    private const string LiveRunMarker = "window.startLiveRun = startLiveRun";

    /// <summary>
    /// Devolve o conteúdo do bloco <c>&lt;script&gt;</c> que alberga o IIFE do
    /// Dashboard (o que contém <see cref="LiveRunMarker"/>).
    /// </summary>
    public static string ExtractFrom(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var marker = html.IndexOf(LiveRunMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new InvalidOperationException(
                "Marcador do Live Run nao encontrado no HTML servido em GET /. " +
                "O harness DC-8 precisa do bloco <script> com 'window.startLiveRun = startLiveRun'.");
        }

        var open = html.LastIndexOf("<script", marker, StringComparison.Ordinal);
        if (open < 0)
        {
            throw new InvalidOperationException("Bloco <script> do Dashboard nao encontrado.");
        }

        var openEnd = html.IndexOf('>', open);
        if (openEnd < 0)
        {
            throw new InvalidOperationException("Tag <script> do Dashboard mal formada.");
        }

        var close = html.IndexOf("</script>", openEnd, StringComparison.Ordinal);
        if (close < 0)
        {
            throw new InvalidOperationException("Fecho </script> do Dashboard nao encontrado.");
        }

        return html.Substring(openEnd + 1, close - openEnd - 1);
    }

    /// <summary>
    /// Injecta, imediatamente antes do fecho do IIFE, um objecto de sondagens
    /// (<c>window.__dc8</c>) que expõe as funções alvo dos testes. A injecção
    /// é feita apenas no texto extraído em memória — nunca na app.
    /// </summary>
    public static string Instrument(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        const string hook = """
    window.__dc8 = {
      apiRequest: apiRequest,
      errorMessageFromBody: errorMessageFromBody,
      startLiveRun: startLiveRun,
      validateSchedCron: validateSchedCron,
      describeSchedCron: describeSchedCron,
      applySchedFrequency: applySchedFrequency,
      openModalPanel: openModalPanel,
      closeModalPanel: closeModalPanel,
      moveOrderingItemToPosition: moveOrderingItemToPosition,
      updateSchedDiscoveryPreview: updateSchedDiscoveryPreview,
      schedDiscValidInt: schedDiscValidInt
    };
""";

        var idx = script.LastIndexOf("})();", StringComparison.Ordinal);
        if (idx < 0)
        {
            throw new InvalidOperationException(
                "Fecho do IIFE ('})();') nao encontrado no bloco <script> do Dashboard.");
        }

        return script.Substring(0, idx) + hook + script.Substring(idx);
    }
}

/// <summary>
/// Wrapper sobre o motor Jint: encapsula o shim DOM, a injecção das sondagens
/// e os acessores usados pelos testes (último pedido fetch, estado de elementos).
/// Uma instância por teste ⇒ estado isolado e determinístico.
/// </summary>
internal sealed class DashboardJsEngine
{
    private readonly Engine _engine;

    private DashboardJsEngine(Engine engine) => _engine = engine;

    public static DashboardJsEngine FromScript(string script)
    {
        var engine = new Engine(options => options.Strict(false));
        try
        {
            engine.Execute(Shim);
            engine.Execute(DashboardJsScript.Instrument(script));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "O IIFE do Dashboard lancou durante a avaliacao no motor Jint. " +
                "Provavelmente falta cobertura no shim DOM. Detalhe: " + ex.Message, ex);
        }

        var wrapper = new DashboardJsEngine(engine);
        if (!wrapper.Eval("!!(window.__dc8)").AsBoolean())
        {
            throw new InvalidOperationException(
                "O IIFE executou mas window.__dc8 nao foi exposto (funcoes alvo renomeadas ou removidas?).");
        }

        return wrapper;
    }

    public JsValue Eval(string expression) => _engine.Evaluate(expression).UnwrapIfPromise();

    public string EvalString(string expression) => Eval(expression).ToString();

    public string? EvalNullableString(string expression)
    {
        var value = Eval(expression);
        return value.IsNull() || value.IsUndefined() ? null : value.ToString();
    }

    public void Exec(string code) => _engine.Execute(code);

    public bool HasProbe(string name) =>
        EvalBool($"typeof window.__dc8.{name} === 'function'");

    /// <summary>Avalia uma expressão JS e devolve-a como booleano (via JSON).</summary>
    public bool EvalBool(string expression) =>
        EvalString($"JSON.stringify(!!({expression}))") == "true";

    /// <summary>Configura o comportamento do próximo <c>fetch</c> (shim).</summary>
    public void SetFetch(string mode, int status = 200, string body = "{}", string? message = null)
    {
        var config = JsonSerializer.Serialize(new
        {
            mode,
            status,
            body,
            message,
        });
        Exec($"window.__test.setFetch({config})");
    }

    public void SetElementValue(string id, string value) =>
        Exec($"window.__test.el({JsString(id)}).value = {JsString(value)};");

    public void SeedAttribute(string attribute, string value, string tag = "input") =>
        Exec($"window.__test.seed({{tag: {JsString(tag)}, {JsString(attribute)}: {JsString(value)}}});");

    public string? LastRequestBodyFor(string url) =>
        EvalNullableString($"window.__test.lastBodyFor({JsString(url)})");

    public string? LastRequestUrl() =>
        EvalNullableString("window.__test.lastUrl()");

    public string QueryValue(string selector) =>
        EvalString($"window.__test.q({JsString(selector)}).value");

    private static string JsString(string value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// Shim DOM/Web mínimo, in-process. Não é um motor de renderização: é um
    /// conjunto de objectos que satisfazem as leituras/escritas que o IIFE faz
    /// no arranque e nos caminhos exercitados. Os timers nunca disparam e o
    /// <c>fetch</c> é integralmente controlado pelo teste (determinismo).
    /// </summary>
    private const string Shim = """
globalThis.window = globalThis;
globalThis.self = globalThis;

var __els = [];
function __register(el) { __els.push(el); return el; }

function ClassList(el) { this._el = el; }
ClassList.prototype.add = function () { for (var i = 0; i < arguments.length; i++) this._el._classes[arguments[i]] = true; };
ClassList.prototype.remove = function () { for (var i = 0; i < arguments.length; i++) delete this._el._classes[arguments[i]]; };
ClassList.prototype.contains = function (c) { return this._el._classes[c] === true; };
ClassList.prototype.toggle = function (c, force) {
  if (force === undefined) { if (this._el._classes[c]) delete this._el._classes[c]; else this._el._classes[c] = true; }
  else { if (force) this._el._classes[c] = true; else delete this._el._classes[c]; }
  return this._el._classes[c] === true;
};
ClassList.prototype.item = function (i) { return Object.keys(this._el._classes)[i] || null; };

function Element(tag, id) {
  this.tagName = String(tag || 'div').toUpperCase();
  this.id = id || '';
  this.children = [];
  this.childNodes = this.children;
  this.parentNode = null;
  this.nextSibling = null;
  this.firstChild = null;
  this.hidden = false;
  this.disabled = false;
  this.value = '';
  this.textContent = '';
  this.innerHTML = '';
  this.className = '';
  this.type = '';
  this.checked = false;
  this.dataset = {};
  this.style = {};
  this._classes = {};
  this.classList = new ClassList(this);
  this._attrs = {};
  this._listeners = {};
  this.onclick = null;
  this.onchange = null;
  this.oninput = null;
  __register(this);
}
Element.prototype.appendChild = function (c) { if (c.parentNode) c.remove(); this.children.push(c); c.parentNode = this; this.firstChild = this.children[0] || null; c.nextSibling = null; return c; };
Element.prototype.insertBefore = function (n, ref) { this.children.push(n); n.parentNode = this; this.firstChild = this.children[0] || null; return n; };
Element.prototype.removeChild = function (c) { var i = this.children.indexOf(c); if (i >= 0) this.children.splice(i, 1); c.parentNode = null; return c; };
Element.prototype.remove = function () { if (this.parentNode) { this.parentNode.removeChild(this); } };
Element.prototype.addEventListener = function (t, fn) { (this._listeners[t] = this._listeners[t] || []).push(fn); };
Element.prototype.removeEventListener = function (t, fn) { var a = this._listeners[t] || []; var i = a.indexOf(fn); if (i >= 0) a.splice(i, 1); };
Element.prototype.dispatchEvent = function (ev) { var a = (this._listeners[ev && ev.type] || []).slice(); for (var i = 0; i < a.length; i++) a[i].call(this, ev); return true; };
Element.prototype.setAttribute = function (k, v) { this._attrs[k] = String(v); if (k.indexOf('data-') === 0) { this.dataset[k.slice(5).replace(/-([a-z])/g, function (m, c) { return c.toUpperCase(); })] = String(v); } };
Element.prototype.getAttribute = function (k) { return this._attrs[k] !== undefined ? this._attrs[k] : null; };
Element.prototype.removeAttribute = function (k) { delete this._attrs[k]; };
Element.prototype.hasAttribute = function (k) { return this._attrs[k] !== undefined; };
Element.prototype.focus = function () { document.activeElement = this; };
Element.prototype.blur = function () { if (document.activeElement === this) document.activeElement = null; };
Element.prototype.click = function () { if (typeof this.onclick === 'function') this.onclick({ target: this, type: 'click' }); };
Element.prototype.getElementsByTagName = function (t) { var out = []; for (var i = 0; i < this.children.length; i++) if (this.children[i].tagName === String(t).toUpperCase()) out.push(this.children[i]); return out; };
Element.prototype.getBoundingClientRect = function () { return { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }; };
Element.prototype.scrollIntoView = function () { };
Element.prototype.closest = function () { return null; };
Element.prototype.contains = function (n) { if (n === this) return true; for (var i = 0; i < this.children.length; i++) if (this.children[i].contains(n)) return true; return false; };

function __matches(el, sel) {
  sel = String(sel).trim();
  if (sel.charAt(0) === '#') return el.id === sel.slice(1);
  if (sel.charAt(0) === '.') {
    var c = sel.slice(1);
    if (el.classList.contains(c)) return true;
    return (' ' + String(el.className || '') + ' ').indexOf(' ' + c + ' ') >= 0;
  }
  var m = /^\[([a-zA-Z0-9_:-]+)(?:=['"]?([^\]'"]+)['"]?)?\]$/.exec(sel);
  if (m) { var v = el.getAttribute(m[1]); if (m[2] === undefined) return v !== null; return v === m[2]; }
  if (/^[a-zA-Z]+$/.test(sel)) return el.tagName === sel.toUpperCase();
  return false;
}
function __walk(root, sel, out) {
  for (var i = 0; i < root.children.length; i++) { var c = root.children[i]; if (__matches(c, sel)) out.push(c); __walk(c, sel, out); }
  return out;
}
Element.prototype.querySelector = function (sel) {
  sel = String(sel).trim();
  if (sel.charAt(0) === '#') return document.getElementById(sel.slice(1));
  var out = __walk(this, sel, []);
  return out.length ? out[0] : null;
};
Element.prototype.querySelectorAll = function (sel) { return __walk(this, String(sel).trim(), []); };

function Document() {
  this.activeElement = null;
  this.hidden = false;
  this.readyState = 'complete';
  this.cookie = '';
  this._byId = {};
  this._listeners = {};
  this.body = new Element('body', '');
  this.documentElement = new Element('html', '');
  this.head = new Element('head', '');
}
Document.prototype.createElement = function (tag) { return new Element(tag, ''); };
Document.prototype.createElementNS = function (ns, tag) { return new Element(tag, ''); };
Document.prototype.createTextNode = function (t) { var e = new Element('text', ''); e.textContent = String(t); return e; };
Document.prototype.getElementById = function (id) { if (!this._byId[id]) { var e = new Element('div', id); this._byId[id] = e; } return this._byId[id]; };
Document.prototype.querySelector = function (sel) {
  sel = String(sel).trim();
  if (sel.charAt(0) === '#') return this.getElementById(sel.slice(1));
  for (var i = 0; i < __els.length; i++) if (__matches(__els[i], sel)) return __els[i];
  var out = __walk(this.body, sel, []);
  return out.length ? out[0] : null;
};
Document.prototype.querySelectorAll = function (sel) { var out = []; for (var i = 0; i < __els.length; i++) if (__matches(__els[i], sel)) out.push(__els[i]); return out; };
Document.prototype.getElementsByTagName = function (t) { var out = []; for (var i = 0; i < __els.length; i++) if (__els[i].tagName === String(t).toUpperCase()) out.push(__els[i]); return out; };
Document.prototype.getElementsByClassName = function (c) { var out = []; for (var i = 0; i < __els.length; i++) if (__els[i].classList.contains(c)) out.push(__els[i]); return out; };
Document.prototype.addEventListener = function (t, fn) { (this._listeners[t] = this._listeners[t] || []).push(fn); };
Document.prototype.removeEventListener = function (t, fn) { var a = this._listeners[t] || []; var i = a.indexOf(fn); if (i >= 0) a.splice(i, 1); };
Document.prototype.dispatchEvent = function (ev) { var a = (this._listeners[ev && ev.type] || []).slice(); for (var i = 0; i < a.length; i++) a[i].call(this, ev); return true; };
Document.prototype.write = function () { };

var document = new Document();

var __fetchBehavior = { mode: 'ok', status: 200, body: '{}' };
var __fetches = [];
function __makeResponse() {
  var status = __fetchBehavior.status;
  return {
    ok: status >= 200 && status < 300,
    status: status,
    statusText: String(status),
    headers: { get: function () { return null; } },
    text: function () { return Promise.resolve(__fetchBehavior.body); },
    json: function () { return Promise.resolve(JSON.parse(__fetchBehavior.body)); }
  };
}
function fetch(url, init) {
  var rec = { url: String(url), init: init || {} };
  __fetches.push(rec);
  window.__lastFetch = rec;
  if (__fetchBehavior.mode === 'reject') {
    return Promise.reject(new Error(__fetchBehavior.message || 'network down'));
  }
  if (__fetchBehavior.mode === 'never') { return new Promise(function () { }); }
  return Promise.resolve(__makeResponse());
}

function setTimeout() { return 0; }
function clearTimeout() { }
function setInterval() { return 0; }
function clearInterval() { }
function requestAnimationFrame() { return 0; }
function cancelAnimationFrame() { }
function addEventListener() { }
function removeEventListener() { }
function confirm() { return true; }
function alert() { }
function prompt() { return null; }
function matchMedia() { return { matches: false, addListener: function () { }, removeListener: function () { }, addEventListener: function () { }, removeEventListener: function () { } }; }
function getComputedStyle() { return {}; }
function Event(type) { this.type = type; }
function CustomEvent(type) { this.type = type; }
function AbortController() { this.signal = {}; this.abort = function () { }; }

var console = { log: function () { }, warn: function () { }, error: function () { }, info: function () { }, debug: function () { } };
var location = { href: 'http://localhost/', pathname: '/', search: '', hash: '', reload: function () { }, assign: function () { } };
var navigator = { userAgent: 'jint-harness', clipboard: { writeText: function () { return Promise.resolve(); } } };
var localStorage = { getItem: function () { return null; }, setItem: function () { }, removeItem: function () { } };
var sessionStorage = localStorage;

if (typeof globalThis.URLSearchParams === 'undefined') {
  globalThis.URLSearchParams = function (init) {
    this._p = [];
    if (typeof init === 'string') {
      var parts = String(init).replace(/^\?/, '').split('&');
      for (var i = 0; i < parts.length; i++) {
        if (!parts[i]) continue;
        var kv = parts[i].split('=');
        this._p.push([decodeURIComponent(kv[0]), decodeURIComponent(kv[1] || '')]);
      }
    }
    this.append = function (k, v) { this._p.push([k, v]); };
    this.set = function (k, v) {
      for (var j = 0; j < this._p.length; j++) { if (this._p[j][0] === k) { this._p[j][1] = v; return; } }
      this._p.push([k, v]);
    };
    this.get = function (k) { for (var j = 0; j < this._p.length; j++) if (this._p[j][0] === k) return this._p[j][1]; return null; };
    this.toString = function () { return this._p.map(function (x) { return x[0] + '=' + x[1]; }).join('&'); };
  };
}

window.__test = {
  setFetch: function (cfg) { __fetchBehavior = cfg || { mode: 'ok', status: 200, body: '{}' }; },
  lastFetch: function () { return window.__lastFetch || null; },
  lastUrl: function () { var f = window.__lastFetch; return f ? f.url : null; },
  lastBodyFor: function (url) { for (var i = __fetches.length - 1; i >= 0; i--) { if (__fetches[i].url === url && typeof __fetches[i].init.body === 'string') return __fetches[i].init.body; } return null; },
  fetchCountFor: function (url) { var n = 0; for (var i = 0; i < __fetches.length; i++) if (__fetches[i].url === url) n++; return n; },
  el: function (id) { return document.getElementById(id); },
  body: function () { return document.body; },
  seed: function (attrs) { var e = document.createElement(attrs.tag || 'input'); for (var k in attrs) { if (k === 'tag') continue; e.setAttribute(k, attrs[k]); } return e; },
  q: function (sel) { return document.querySelector(sel); },
  json: function (v) { return JSON.stringify(v); },
  jsonAsync: async function (p) { var v = await p; return JSON.stringify(v); }
};
""";
}
