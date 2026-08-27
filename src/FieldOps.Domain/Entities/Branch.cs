using FieldOps.Domain.Common;

namespace FieldOps.Domain.Entities;

public sealed class Branch : Entity
{
    private Branch(string name)
    {
        Name = RequiredText(name, nameof(name));
    }

    public string Name { get; }

    public static Branch Create(string name) => new(name);
}