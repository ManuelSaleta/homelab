namespace Jarvis.Sharp.Slack;

using Jarvis.Sharp.State;
using SlackNet.Blocks;

public static class BlockKitBuilder
{
    public static IList<Block> BuildCandidateBlocks(CandidateBatch batch)
    {
        var blocks = new List<Block>
        {
            new HeaderBlock
            {
                Text = new PlainText($"🎬 media-sync found {batch.Items.Count} candidates ({batch.BatchId}):")
            },
            new DividerBlock()
        };

        foreach (var item in batch.Items)
        {
            var yearStr = item.Year.HasValue ? $" ({item.Year.Value})" : string.Empty;
            blocks.Add(new SectionBlock
            {
                Text = new Markdown($"*[{item.Index}]* *{item.Title}*{yearStr} — _{item.MediaType}_")
            });
        }

        blocks.Add(new DividerBlock());
        
        var contextBlock = new ContextBlock();
        contextBlock.Elements.Add(new Markdown("Reply with `/download <numbers>` (e.g. `/download 1 3`) to initiate acquisition."));
        blocks.Add(contextBlock);

        return blocks;
    }

    public static IList<Block> BuildAlertTriageBlocks(
        string alertText,
        string ns,
        string deployment,
        string pod,
        string diagnosis)
    {
        var targetJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            @namespace = ns,
            deployment = deployment,
            pod = pod
        });

        var actionsBlock = new ActionsBlock();
        actionsBlock.Elements.Add(new Button
        {
            ActionId = "remediate_restart_deployment",
            Text = new PlainText("🔄 Restart Deployment"),
            Style = ButtonStyle.Danger,
            Value = targetJson
        });
        actionsBlock.Elements.Add(new Button
        {
            ActionId = "remediate_view_logs",
            Text = new PlainText("📜 View Full Logs"),
            Value = targetJson
        });
        actionsBlock.Elements.Add(new Button
        {
            ActionId = "remediate_dismiss",
            Text = new PlainText("❌ Dismiss"),
            Value = targetJson
        });

        return new List<Block>
        {
            new HeaderBlock
            {
                Text = new PlainText("🚨 Homelab Incident Triage (Jarvis)")
            },
            new SectionBlock
            {
                Text = new Markdown($"*Alert Details:*\n```{alertText}```")
            },
            new SectionBlock
            {
                Text = new Markdown($"*Target Context:*\n• *Namespace:* `{ns}`\n• *Deployment:* `{deployment}`\n• *Pod:* `{pod}`")
            },
            new SectionBlock
            {
                Text = new Markdown($"*Jarvis Analysis & Proposed Action:*\n{diagnosis}")
            },
            new DividerBlock(),
            actionsBlock
        };
    }
}
