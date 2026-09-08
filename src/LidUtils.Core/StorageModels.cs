namespace LidUtils.Core;

/// <summary>A catalog-validated item payload that can be materialized into account storage.</summary>
public sealed record StorageItemTemplate(
    int Type,
    string DefinitionId,
    string Name,
    string EntityJson,
    string? LinkedMushroomJson = null);

public abstract record StorageOperation;

/// <summary>Adds empty account-storage slots, capped at a total account-storage capacity of 2000.</summary>
public sealed record ExpandStorageOperation(int SlotCount = 10) : StorageOperation
{
    public const int MaxTotalSlots = 2000;
    public static readonly IReadOnlyList<int> AllowedSlotCounts = [10, 20, 50, 100];
}

/// <summary>Creates an item instance and places it in an existing locker slot.</summary>
public sealed record SetStorageSlotOperation(int Slot, StorageItemTemplate Template) : StorageOperation;

/// <summary>Empties an existing locker slot, removing its instance only when it has no other reference.</summary>
public sealed record ClearStorageSlotOperation(int Slot) : StorageOperation;

public sealed record StorageSlot(
    int Slot,
    int Type,
    string? EntityId,
    string? DefinitionId = null,
    string? Name = null)
{
    public bool IsOccupied => !string.IsNullOrWhiteSpace(EntityId);
}

public sealed record StorageInventory(int PlayerUid, IReadOnlyList<StorageSlot> Slots)
{
    public int Capacity => Slots.Count;
    public int OccupiedCount => Slots.Count(slot => slot.IsOccupied);
}
