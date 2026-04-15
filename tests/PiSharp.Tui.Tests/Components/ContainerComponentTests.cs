namespace PiSharp.Tui.Tests;

public sealed class ContainerComponentTests
{
    [Fact]
    public void AddChild_AppearsInChildren()
    {
        var container = new ContainerComponent();
        var child = new Text( "hello");

        container.AddChild(child);

        Assert.Single(container.Children);
        Assert.Same(child, container.Children[0]);
    }

    [Fact]
    public void RemoveChild_RemovesFromChildren()
    {
        var container = new ContainerComponent();
        var child = new Text( "hello");
        container.AddChild(child);

        var removed = container.RemoveChild(child);

        Assert.True(removed);
        Assert.Empty(container.Children);
    }

    [Fact]
    public void RemoveChild_ReturnsFalse_WhenChildNotFound()
    {
        var container = new ContainerComponent();
        var child = new Text( "hello");

        Assert.False(container.RemoveChild(child));
    }

    [Fact]
    public void Clear_RemovesAllChildren()
    {
        var container = new ContainerComponent();
        container.AddChild(new Text( "a"));
        container.AddChild(new Text( "b"));

        container.Clear();

        Assert.Empty(container.Children);
    }

    [Fact]
    public void Render_CombinesChildrenOutput()
    {
        var container = new ContainerComponent();
        container.AddChild(new Text( "first"));
        container.AddChild(new Text( "second"));

        var lines = container.Render(new RenderContext(20));

        Assert.Contains(lines, l => l.Contains("first"));
        Assert.Contains(lines, l => l.Contains("second"));
    }

    [Fact]
    public void ChildInvalidation_PropagesToContainer()
    {
        var container = new ContainerComponent();
        var child = new Text( "hello");
        container.AddChild(child);

        var invalidated = false;
        container.Invalidated += (_, _) => invalidated = true;

        child.Invalidate();

        Assert.True(invalidated);
    }

    [Fact]
    public void RemovedChild_DoesNotPropagateInvalidation()
    {
        var container = new ContainerComponent();
        var child = new Text( "hello");
        container.AddChild(child);
        container.RemoveChild(child);

        var invalidated = false;
        container.Invalidated += (_, _) => invalidated = true;

        child.Invalidate();

        Assert.False(invalidated);
    }

    [Fact]
    public void ClearedChildren_DoNotPropagateInvalidation()
    {
        var container = new ContainerComponent();
        var child = new Text( "hello");
        container.AddChild(child);
        container.Clear();

        var invalidated = false;
        container.Invalidated += (_, _) => invalidated = true;

        child.Invalidate();

        Assert.False(invalidated);
    }
}
