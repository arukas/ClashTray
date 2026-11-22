using ClashTray.Presentation;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuleRowsTests
{
    [TestMethod]
    public void RuleProjectionFormatsSearchAndDisplayText()
    {
        RuleProjection projection = new RuleProjection(0, new RuleInfo("DOMAIN", "example.test", "DIRECT", 0));

        Assert.AreEqual("DOMAIN example.test DIRECT", projection.SearchText);
        Assert.AreEqual("DOMAIN  example.test  → DIRECT", projection.DisplayText);
    }

    [TestMethod]
    public void DuplicateRulesRemainDistinctWhenFiltered()
    {
        RuleProjection[] source = CreateDuplicateRules();
        string controller = "local:1";
        StableRowReconciler<RuleRowIdentity, RuleProjection, RuleRowViewModel> rows =
            new StableRowReconciler<RuleRowIdentity, RuleProjection, RuleRowViewModel>(
                projection => new RuleRowIdentity(controller, projection.SourceIndex));
        Reconcile(rows, source, source, controller);

        RuleRowViewModel originalFirst = rows.Rows[0];
        RuleRowViewModel originalSecond = rows.Rows[1];
        Reconcile(rows, source, [source[0]], controller);

        Assert.AreEqual(1, rows.Rows.Count);
        Assert.AreSame(originalFirst, rows.Rows[0]);
        Assert.AreNotSame(originalSecond, rows.Rows[0]);
    }

    [TestMethod]
    public void NewControllerGenerationDoesNotReuseRows()
    {
        RuleProjection[] source = CreateDuplicateRules();
        string controller = "local:1";
        StableRowReconciler<RuleRowIdentity, RuleProjection, RuleRowViewModel> rows =
            new StableRowReconciler<RuleRowIdentity, RuleProjection, RuleRowViewModel>(
                projection => new RuleRowIdentity(controller, projection.SourceIndex));
        Reconcile(rows, source, source, controller);
        RuleRowViewModel original = rows.Rows[0];

        controller = "local:2";
        Reconcile(rows, source, source, controller);

        Assert.AreNotSame(original, rows.Rows[0]);
    }

    [TestMethod]
    public void UnchangedProjectionDoesNotNotify()
    {
        RuleProjection projection = new RuleProjection(0, new RuleInfo("DOMAIN", "example.test", "DIRECT", 0));
        RuleRowViewModel row = new RuleRowViewModel(projection);
        int notifications = 0;
        row.PropertyChanged += (_, _) => notifications++;

        Assert.IsFalse(row.Update(new RuleProjection(0, projection.Rule)));
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public void ChangedProjectionUpdatesTextAndNotifies()
    {
        RuleRowViewModel row = new RuleRowViewModel(
            new RuleProjection(0, new RuleInfo("DOMAIN", "example.test", "DIRECT", 0)));
        List<string> changedProperties = new List<string>();
        row.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName ?? string.Empty);

        Assert.IsTrue(row.Update(new RuleProjection(0, new RuleInfo("DOMAIN", "example.org", "DIRECT", 0))));
        Assert.AreEqual("DOMAIN  example.org  → DIRECT", row.DisplayText);
        CollectionAssert.Contains(changedProperties, nameof(RuleRowViewModel.Rule));
        CollectionAssert.Contains(changedProperties, nameof(RuleRowViewModel.DisplayText));
    }

    private static RuleProjection[] CreateDuplicateRules() =>
        [
            new RuleProjection(0, new RuleInfo("DOMAIN", "duplicate.test", "DIRECT", 0)),
            new RuleProjection(1, new RuleInfo("DOMAIN", "duplicate.test", "DIRECT", 0))
        ];

    private static void Reconcile(
        StableRowReconciler<RuleRowIdentity, RuleProjection, RuleRowViewModel> rows,
        IReadOnlyList<RuleProjection> source,
        IReadOnlyList<RuleProjection> visible,
        string controller)
    {
        rows.Reconcile(
            source,
            visible.Select(projection => new RuleRowIdentity(controller, projection.SourceIndex)).ToArray(),
            projection => new RuleRowViewModel(projection),
            (row, projection) => row.Update(projection));
    }
}
