using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class DashboardModalUiHtmlTests
{
    private static string BuildDashboardHtml()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }

    [Fact]
    public void Modal_primitive_is_present()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='modalRoot'", html);
        Assert.Contains("function openModalPanel(", html);
        Assert.Contains("function closeModalPanel(", html);
        Assert.Contains("window.openModalPanel = openModalPanel;", html);
        Assert.Contains("window.closeModalPanel = closeModalPanel;", html);
        Assert.Contains("e.target === root", html);
        Assert.Contains("e.key === 'Escape'", html);
    }

    [Fact]
    public void Modal_css_is_present()
    {
        var html = BuildDashboardHtml();

        Assert.Contains(".modal-panel", html);
        Assert.Contains("body.modal-open", html);
        Assert.Contains("#modalRoot", html);
    }

    [Fact]
    public void All_inline_editors_open_via_modal()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("openModalPanel('createChannelForm')", html);
        Assert.Contains("openModalPanel('channelDetailPanel')", html);
        Assert.Contains("openModalPanel('reviewApproveModal')", html);
        Assert.Contains("openModalPanel('addRuleForm')", html);
        Assert.Contains("openModalPanel('addAffinityForm')", html);
        Assert.Contains("openModalPanel('orderingEditForm')", html);
        Assert.Contains("openModalPanel('scheduledJobForm')", html);
        Assert.Contains("openModalPanel('orderingCreateForm')", html);
        Assert.Contains("openModalPanel('channelSourceSelectionPolicyForm')", html);
        Assert.Contains("openModalPanel('importPolicyEditForm')", html);
        Assert.Contains("openModalPanel('canonicalGroupEditForm')", html);

        Assert.Contains("closeModalPanel()", html);
    }

    [Fact]
    public void Canonical_group_editor_modal_replaces_group_mappings_ui()
    {
        var html = BuildDashboardHtml();

        // Editor modal de grupos canónicos: criar + editar/renomear +
        // marcar default + activar/desactivar + ordem.
        Assert.Contains("id='canonicalGroupEditForm'", html);
        Assert.Contains("id='cgKey'", html);
        Assert.Contains("id='cgName'", html);
        Assert.Contains("id='cgCountry'", html);
        Assert.Contains("id='cgOrder'", html);
        Assert.Contains("id='cgDefault'", html);
        Assert.Contains("id='cgEnabled'", html);
        Assert.Contains("onclick='showCreateCanonicalGroup()'", html);
        Assert.Contains("openModalPanel('canonicalGroupEditForm')", html);

        // Handlers inline exportados para window.
        Assert.Contains("window.showCreateCanonicalGroup = showCreateCanonicalGroup;", html);
        Assert.Contains("window.editCanonicalGroup = editCanonicalGroup;", html);
        Assert.Contains("window.submitCanonicalGroupEdit = submitCanonicalGroupEdit;", html);
        Assert.Contains("function submitCanonicalGroupEdit(", html);
        Assert.Contains("onclick='editCanonicalGroup(${g.id})'", html);

        // A UI obsoleta de group-mappings foi retirada do Dashboard.
        Assert.DoesNotContain("groupMappingsTable", html);
        Assert.DoesNotContain("data-mapping-create", html);
        Assert.DoesNotContain("Group Mappings", html);
        Assert.DoesNotContain("data-group-create", html);
    }

    [Fact]
    public void Import_policies_tab_is_hidden_pending_w6b3()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("data-ctab='policies' hidden", html);
        Assert.DoesNotContain("metricCard('Import Policies'", html);
        Assert.Contains("openModalPanel('importPolicyEditForm')", html);
    }
}
