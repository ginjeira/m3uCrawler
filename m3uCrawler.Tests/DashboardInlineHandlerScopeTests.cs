using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W1 — Regressão estrutural contra a reintrodução da falha de escopo dos
/// handlers inline do Dashboard.
///
/// O script principal do Dashboard é envolvido numa IIFE
/// (<c>(function(){ … })();</c>), pelo que as funções declaradas dentro
/// dele <b>não</b> são globais. Os atributos inline do HTML
/// (<c>onclick</c>/<c>onchange</c>/<c>oninput</c>/…) são avaliados em
/// escopo global; qualquer função não re-exportada para <c>window</c>
/// lança <c>ReferenceError</c> e a acção não executa.
///
/// Este teste NÃO verifica apenas as funções conhecidas: percorre
/// <b>todos</b> os handlers inline do HTML renderizado, extrai os
/// identificadores de função que eles invocam e exige que cada um esteja
/// disponível em <c>window</c> (ou seja um builtin/palavra-chave seguro).
/// Cobre, assim, a classe inteira da regressão.
/// </summary>
public class DashboardInlineHandlerScopeTests
{
    /// <summary>Renderiza o HTML do Dashboard via reflection (o método é privado).</summary>
    private static string BuildDashboardHtml()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }

    /// <summary>
    /// Identificadores que NÃO são funções globais do Dashboard: palavras-chave
    /// da linguagem e builtins do browser. Se um handler inline os invocar, não é
    /// necessário (nem correcto) estarem em <c>window</c>.
    /// </summary>
    private static readonly HashSet<string> NonGlobalTokens = new(StringComparer.Ordinal)
    {
        // Palavras-chave / literais
        "event", "this", "return", "if", "else", "for", "while", "do", "switch",
        "case", "break", "continue", "try", "catch", "finally", "throw", "new",
        "delete", "typeof", "void", "in", "of", "instanceof", "function", "await",
        "async", "yield", "super", "true", "false", "null", "undefined",
        // Builtins do browser / JS
        "window", "document", "location", "history", "navigator", "console",
        "alert", "confirm", "prompt", "fetch", "setTimeout", "setInterval",
        "clearTimeout", "clearInterval", "queueMicrotask",
        "JSON", "Object", "Array", "Number", "String", "Boolean", "Date", "Math",
        "Promise", "RegExp", "Error", "Map", "Set", "URL", "URLSearchParams",
        "parseInt", "parseFloat", "isNaN", "isFinite",
        "encodeURIComponent", "decodeURIComponent", "encodeURI", "decodeURI",
        "btoa", "atob", "Blob", "FormData", "Headers", "Request", "Response",
    };

    private static readonly Regex HandlerAttribute = new(
        @"\bon(?:click|change|input|submit|keydown|keyup|keypress|dblclick|blur|focus|load|error)\s*=\s*(['""])(?<body>.*?)\1",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Chamada de função: identificador seguido de <c>(</c>, não precedido de
    /// <c>.</c> (método), <c>$</c> (interpolação template) ou caractere de
    /// palavra (parte de outro identificador).
    /// </summary>
    private static readonly Regex FunctionCall = new(
        @"(?<![.\w$])(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*\(");

    private static readonly Regex WindowExport = new(
        @"\bwindow\.(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*=");

    /// <summary>
    /// Interpolação de template literal (<c>${...}</c>). Em handlers inline gerados
    /// por strings JS (ex.: <c>`&lt;button onclick='foo(${escapeAttr(id)})'&gt;`</c>),
    /// o conteúdo de <c>${...}</c> é avaliado no momento de construção do HTML
    /// (dentro do IIFE), NÃO no clique. Removê-lo antes de extrair os
    /// identificadores evita classificar helpers de escape como handlers.
    /// </summary>
    private static readonly Regex TemplateInterpolation = new(
        @"\$\{[^{}]*\}", RegexOptions.Singleline);

    private static string StripInterpolations(string handlerBody)
    {
        var current = handlerBody;
        string previous;
        do
        {
            previous = current;
            current = TemplateInterpolation.Replace(current, string.Empty);
        }
        while (!string.Equals(current, previous, StringComparison.Ordinal));

        return current;
    }

    [Fact]
    public void Every_inline_handler_function_is_available_on_window()
    {
        var html = BuildDashboardHtml();

        var exported = new HashSet<string>(
            WindowExport.Matches(html).Select(m => m.Groups["name"].Value),
            StringComparer.Ordinal);

        var handlerBodies = HandlerAttribute.Matches(html)
            .Select(m => m.Groups["body"].Value)
            .ToList();

        // Guardas anti-falso-verde: o scan tem de estar a ver HTML real.
        Assert.True(handlerBodies.Count >= 50,
            $"Esperava >= 50 handlers inline no Dashboard, encontrei {handlerBodies.Count}. " +
            "Se o HTML mudou de forma legítima, ajustar o limiar — não remover a verificação.");
        Assert.True(exported.Count >= 60,
            $"Esperava >= 60 exports em window, encontrei {exported.Count}.");

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var body in handlerBodies)
        {
            foreach (Match call in FunctionCall.Matches(StripInterpolations(body)))
            {
                var name = call.Groups["name"].Value;
                if (NonGlobalTokens.Contains(name))
                {
                    continue;
                }

                if (!exported.Contains(name))
                {
                    missing.Add(name);
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "O HTML do Dashboard chama funções inline que não estão disponíveis em `window` " +
            "(o script principal é uma IIFE). Adicionar `window.<name> = <name>;` para: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void Dashboard_has_no_inline_handler_that_calls_a_truly_undefined_function()
    {
        // Complemento: além de estar em window, o nome tem de corresponder a uma
        // declaração de função no próprio HTML/JS. Pega identificadores chamados
        // em inline handler que não existem em lado nenhum (categoria b — bug
        // diferente, não apenas escopo).
        var html = BuildDashboardHtml();

        var definedFunctions = new HashSet<string>(
            Regex.Matches(
                    html,
                    @"(?:^|\s)(?:async\s+)?function\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*\(")
                .Select(m => m.Groups["name"].Value),
            StringComparer.Ordinal);

        var missingDefinition = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match attr in HandlerAttribute.Matches(html))
        {
            foreach (Match call in FunctionCall.Matches(StripInterpolations(attr.Groups["body"].Value)))
            {
                var name = call.Groups["name"].Value;
                if (NonGlobalTokens.Contains(name))
                {
                    continue;
                }

                if (!definedFunctions.Contains(name))
                {
                    missingDefinition.Add(name);
                }
            }
        }

        Assert.True(
            missingDefinition.Count == 0,
            "Handlers inline chamam identificadores que não correspondem a nenhuma " +
            "função declarada no script do Dashboard: " +
            string.Join(", ", missingDefinition));
    }
}
