namespace LidUtils.Core;

/// <summary>A catalog-validated item payload that can be materialized into account storage.</summary>
public sealed record StorageItemTemplate(
    int Type,
    string DefinitionId,
    string Name,
    string EntityJson,
    string? LinkedMushroomJson = null);

public abstract record StorageOperation;

/// <summary>Adds empty account-storage slots, capped at a total account-storage capacity of 1500.</summary>
public sealed record ExpandStorageOperation(int SlotCount = 10) : StorageOperation
{
    public const int MaxTotalSlots = 1500;
    public static readonly IReadOnlyList<int> AllowedSlotCounts = [10, 20, 50, 100];
}

/// <summary>Creates an item instance and places it in an existing locker slot.</summary>
public sealed record SetStorageSlotOperation(int Slot, StorageItemTemplate Template) : StorageOperation;

/// <summary>Empties an existing locker slot, removing its instance only when it has no other reference.</summary>
public sealed record ClearStorageSlotOperation(int Slot) : StorageOperation;

/// <summary>
/// Adds empty Death Bag slots to every owned fighter, mirroring the Royal Express VIP
/// activation bonus. The Death Bag has no scalar capacity pointer; its capacity is the
/// number of slot rows stored per fighter under /soul/deathbag/&lt;player uid&gt;/&lt;cid&gt;.
/// </summary>
public sealed record ExpandDeathBagsOperation(int RowsPerBag = 10) : StorageOperation
{
    /// <summary>Absolute Death Bag ceiling after the Royal Express VIP bonus.</summary>
    public const int MaximumRowsPerBag = 80;
}

/// <summary>Adds empty Death Bag slots to one owned fighter, reserving ten slots for VIP.</summary>
public sealed record ExpandCharacterDeathBagOperation(string CharacterId, int SlotCount = 5) : StorageOperation
{
    public const int MaximumRowsPerBag = 70;
    public static readonly IReadOnlyList<int> AllowedSlotCounts = [1, 5, 10];
}

/// <summary>
/// Sets one owned fighter's Death Bag to an exact number of slot rows, growing or shrinking it to
/// restore the class/grade default from master_body_detail (plus the VIP bonus when applicable).
/// Shrinking never removes an occupied slot, so carried items are never silently dropped.
/// </summary>
public sealed record SetCharacterDeathBagCapacityOperation(string CharacterId, int SlotCount) : StorageOperation
{
    public const int MinimumRowsPerBag = 1;
    public const int MaximumRowsPerBag = ExpandDeathBagsOperation.MaximumRowsPerBag;
}

/// <summary>
/// Creates a player-owned item instance and places it in an existing Death Bag slot of one
/// fighter. The materialized entity belongs to the active player, unlike account-storage items
/// which are locker-owned, so an occupied slot is replaced only when its old entity is not
/// referenced anywhere else.
/// </summary>
public sealed record SetCharacterDeathBagSlotOperation(string CharacterId, int Slot, StorageItemTemplate Template) : StorageOperation;

/// <summary>
/// Empties an existing fighter Death Bag slot, removing its player-owned instance only when it
/// has no other reference.
/// </summary>
public sealed record ClearCharacterDeathBagSlotOperation(string CharacterId, int Slot) : StorageOperation;

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
