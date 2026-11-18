namespace MetroOps.Contracts;

public sealed record StationDiagnosis(string StationId, string StationName, string ScenarioId, int Generation, long Version,
    DateTimeOffset SimTime, DateTimeOffset UpdatedAt, string Status, string Assessment,
    StationQueueObservation? Queue, int TrainCount, int TrainLoad, int TrainCapacity, int FullTrainCount, int HeldTrainCount,
    IReadOnlyList<StationTrainObservation> Trains, int RestrictionCount, IReadOnlyList<StationRestrictionObservation> Restrictions,
    int ReservedBlockCount, IReadOnlyList<StationBlockObservation> ReservedBlocks);

public sealed record StationQueueObservation(int Waiting, int PlatformWaiting, int AccessQueue, int ReadyTransfers, int PlatformCapacity);
public sealed record StationTrainObservation(string Id, string Line, string State, int Load, int Capacity, string? HoldReason);
public sealed record StationRestrictionObservation(string Kind, string Target);
public sealed record StationBlockObservation(string Block, string TrainId);
