namespace Zombies.Domain.Inventory;

public readonly record struct ContainerId(long Value)
{
    public override string ToString() => $"container-{Value}";
}

/// <summary>Identifies a Stack within one Container.</summary>
public readonly record struct StackId(int Value)
{
    public override string ToString() => $"stack-{Value}";
}
