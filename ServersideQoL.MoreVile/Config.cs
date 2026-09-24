using BepInEx.Configuration;

namespace ServersideQoL.MoreVile;

public sealed class Config(ConfigFile cfg, Logger logger) : ConfigBase<Config>(cfg, logger)
{
  public override ConfigEntry<bool> Enabled { get; } = BindEx(cfg, true,
    "Enables/disables the entire mod");

  public ConfigEntry<float> SpawnChanceMultiplier { get; } = BindEx(cfg, 2f, """
    Multiplier for the chance of regular (0-star) Viles to spawn naturally.
    Example: 2 doubles the chance, 1 leaves it unchanged.
    1-star and 2-star Viles are spawned in addition to this, see the options below.
    Spawn conditions (biome, time of day, required boss progress, etc.) are the same as in vanilla.
    """, new AcceptableValueRange<float>(1f, 20f));

  public ConfigEntry<bool> SpawnOneStarViles { get; } = BindEx(cfg, true, """
    True to let 1-star Viles spawn naturally.
    In vanilla, they can only be spawned with the developer console.
    """);

  public ConfigEntry<float> OneStarSpawnChanceMultiplier { get; } = BindEx(cfg, 2f, $"""
    Chance of 1-star Viles to spawn naturally, as a multiple of the vanilla natural spawn chance of regular Viles.
    Example: 1 makes 1-star Viles spawn as often as regular Viles do in vanilla, 0.5 half as often.
    Only used if {nameof(SpawnOneStarViles)} is true.
    """, new AcceptableValueRange<float>(0f, 20f));

  public ConfigEntry<bool> SpawnTwoStarViles { get; } = BindEx(cfg, true, """
    True to let 2-star Viles spawn naturally.
    In vanilla, they can only be spawned with the developer console.
    """);

  public ConfigEntry<float> TwoStarSpawnChanceMultiplier { get; } = BindEx(cfg, 2f, $"""
    Chance of 2-star Viles to spawn naturally, as a multiple of the vanilla natural spawn chance of regular Viles.
    Example: 1 makes 2-star Viles spawn as often as regular Viles do in vanilla, 0.5 half as often.
    Only used if {nameof(SpawnTwoStarViles)} is true.
    """, new AcceptableValueRange<float>(0f, 20f));

  public ConfigEntry<float> MaxSpawnedMultiplier { get; } = BindEx(cfg, 1f, """
    Multiplier for the maximum number of Viles which can be around a player before no more spawn naturally.
    With a higher spawn chance, this limit is reached sooner, so you might want to increase it as well.
    """, new AcceptableValueRange<float>(1f, 10f));
}
