using UnityEngine;

namespace ServersideQoL.MoreVile;

/// <summary>
/// Natural spawns are rolled by the clients' <see cref="SpawnSystem"/>, which a serverside mod cannot change.
/// Instead, this processor rolls the additional spawn chance on the server, using the vanilla spawn data of Viles.
/// </summary>
[Processor(Id)]
public sealed class VileProcessor : Processor<VileProcessor.PrefabInfo>
{
  public const string Id = "a8c4a115-80bb-4288-82d6-6e08a1c57910";
  const string VilePrefabName = "Unbjorn";
  static readonly int __vilePrefab = VilePrefabName.GetStableHashCode();

  // Vanilla defaults used by SpawnSystem when m_spawnRadiusMin/Max are not set
  const float DefaultSpawnRadiusMin = 40f;
  const float DefaultSpawnRadiusMax = 80f;
  const int MaxSpawnPointTries = 10;

  readonly List<SpawnSystem.SpawnData> _spawnData = [];
  readonly Dictionary<int, float> _noSpawnAreaRadiusByPrefab = [];
  readonly Dictionary<(long PeerID, int SpawnDataIndex), float> _nextRollTime = [];
  readonly List<ZDO> _sectorObjects = [];
  readonly List<(float ChanceMultiplier, int? Level)> _variants = [];

  public sealed record PrefabInfo(Character Character) : ProcessorPrefabInfo
  {
    public override bool IsValid => PrefabInfo.PrefabHash == __vilePrefab;
  }

  protected override void Initialize()
  {
    _spawnData.Clear();
    _nextRollTime.Clear();
    foreach (var data in ZoneSystem.instance.m_zoneCtrlPrefab.GetComponent<SpawnSystem>().m_spawnLists.SelectMany(static x => x.m_spawners))
    {
      if (data.m_enabled && !data.m_devDisabled && data.m_prefab is not null && data.m_prefab.name is VilePrefabName)
        _spawnData.Add(data);
    }

    if (_spawnData.Count is 0)
      Logger.LogWarning($"No natural spawn data found for {VilePrefabName}");

    foreach (var data in _spawnData)
    {
      var key = string.IsNullOrEmpty(data.m_requiredGlobalKey) ? "none" :
        $"{data.m_requiredGlobalKey} ({(ZoneSystem.instance.GetGlobalKey(data.m_requiredGlobalKey) ? "set" : "NOT set")})";
      Logger.LogInfo($"{VilePrefabName} spawn data '{data.m_name}': biome: {data.m_biome}, area: {data.m_biomeArea}, " +
        $"day: {data.m_spawnAtDay}, night: {data.m_spawnAtNight}, chance: {data.m_spawnChance}%, interval: {data.m_spawnInterval}s, " +
        $"max spawned: {data.m_maxSpawned}, group: {data.m_groupSizeMin}-{data.m_groupSizeMax}, levels: {data.m_minLevel}-{data.m_maxLevel}, " +
        $"required key: {key}, required environments: [{string.Join(", ", data.m_requiredEnvironments ?? [])}], " +
        $"required event: '{data.m_requiredPersistentEvent}', altitude: {data.m_minAltitude}-{data.m_maxAltitude}, " +
        $"forest: in {data.m_inForest}/out {data.m_outsideForest}, radius: {data.m_spawnRadiusMin}-{data.m_spawnRadiusMax}");
    }

    _noSpawnAreaRadiusByPrefab.Clear();
    foreach (var prefab in ZNetScene.instance.m_prefabs)
    {
      foreach (var area in prefab.GetComponentsInChildren<EffectArea>(true))
      {
        if ((area.m_type & (EffectArea.Type.PlayerBase | EffectArea.Type.NoMonsters)) is 0 || area.GetComponent<SphereCollider>() is not { } collider)
          continue;
        var scale = area.transform.lossyScale;
        var radius = collider.radius * Mathf.Max(scale.x, scale.z);
        var hash = prefab.name.GetStableHashCode();
        _noSpawnAreaRadiusByPrefab[hash] = Mathf.Max(radius, _noSpawnAreaRadiusByPrefab.TryGetValue(hash, out var r) ? r : 0);
      }
    }
  }

  protected override void PreProcess(PeersEnumerable peers)
  {
    if (_spawnData.Count is 0)
      return;

    // Each variant is rolled independently, relative to the vanilla spawn chance of regular Viles.
    // Level null: level is rolled like in vanilla, otherwise fixed (level = stars + 1)
    _variants.Clear();
    if (Config.Instance.SpawnChanceMultiplier.Value > 1f)
      _variants.Add((Config.Instance.SpawnChanceMultiplier.Value - 1f, null)); // only the additional chance, vanilla rolls the rest
    if (Config.Instance.SpawnOneStarViles.Value && Config.Instance.OneStarSpawnChanceMultiplier.Value > 0f)
      _variants.Add((Config.Instance.OneStarSpawnChanceMultiplier.Value, 2));
    if (Config.Instance.SpawnTwoStarViles.Value && Config.Instance.TwoStarSpawnChanceMultiplier.Value > 0f)
      _variants.Add((Config.Instance.TwoStarSpawnChanceMultiplier.Value, 3));
    if (_variants.Count is 0)
      return;

    var diagnostics = ServersideQoL.Config.Instance.DiagnosticLogs.Value;
    var now = Time.time;
    foreach (var peer in peers)
    {
      var playerPos = peer.RefPos;
      if (playerPos.y > 3000) // in a dungeon
        continue;

      var sectorObjectsLoaded = false;
      for (int i = 0; i < _spawnData.Count; i++)
      {
        var key = (peer.ZNetPeer.m_uid, i);
        if (_nextRollTime.TryGetValue(key, out var next) && now < next)
          continue;
        var data = _spawnData[i];
        _nextRollTime[key] = now + data.m_spawnInterval;

        if (GetCannotSpawnReason(data) is { } cannotSpawnReason)
        {
          if (diagnostics)
            Logger.LogInfo($"{VilePrefabName} roll near {playerPos}: skipped, {cannotSpawnReason}");
          continue;
        }

        foreach (var (multiplier, level) in _variants)
        {
          // Same roll as SpawnSystem, chances > 100% result in multiple spawns
          var rolls = 0;
          for (var chance = data.m_spawnChance * multiplier; chance > 0; chance -= 100f)
          {
            if (UnityEngine.Random.Range(0f, 100f) <= chance)
              rolls++;
          }
          if (diagnostics)
            Logger.LogInfo($"{VilePrefabName} roll near {playerPos} (level {level?.ToString() ?? "rolled"}): chance {data.m_spawnChance * multiplier}%, {rolls} successful roll(s)");
          if (rolls is 0)
            continue;

          if (!sectorObjectsLoaded)
          {
            _sectorObjects.Clear();
            ZDOMan.instance.FindSectorObjects(peer.GetSector(), ZNet.instance.GetSyncedSimulationDistance(), _sectorObjects);
            sectorObjectsLoaded = true;
          }

          for (int r = 0; r < rolls; r++)
          {
            var maxSpawned = Mathf.RoundToInt(data.m_maxSpawned * Config.Instance.MaxSpawnedMultiplier.Value);
            var nearby = _sectorObjects.Count(static x => x.GetPrefab() == __vilePrefab);
            if (nearby >= maxSpawned)
            {
              if (diagnostics)
                Logger.LogInfo($"{VilePrefabName}: max spawned reached ({nearby}/{maxSpawned})");
              break;
            }
            if (FindSpawnPoint(data, playerPos, peers, diagnostics) is not { } spawnPoint)
              break;
            SpawnGroup(data, spawnPoint, level, diagnostics);
          }
        }
      }
    }
    _sectorObjects.Clear();
  }

  protected override ProcessResult Process(ServersideQoLZDO zdo, IReadOnlyList<Peer> peers, PrefabInfo prefabInfo)
    => ProcessResult.UnregisterProcessor;

  static string? GetCannotSpawnReason(SpawnSystem.SpawnData data)
  {
    if (!string.IsNullOrEmpty(data.m_requiredGlobalKey) && !ZoneSystem.instance.GetGlobalKey(data.m_requiredGlobalKey))
      return $"required global key {data.m_requiredGlobalKey} not set";
    if (!string.IsNullOrEmpty(data.m_requiredPersistentEvent)) // not tracked by this mod, don't spawn to be safe
      return $"requires event {data.m_requiredPersistentEvent}";
    if (!data.m_spawnAtDay && EnvMan.IsDay())
      return "no spawns at day";
    if (!data.m_spawnAtNight && EnvMan.IsNight())
      return "no spawns at night";
    // m_requiredEnvironments is ignored: weather is simulated by the clients and not known reliably on the server
    return null;
  }

  Vector3? FindSpawnPoint(SpawnSystem.SpawnData data, Vector3 playerPos, PeersEnumerable peers, bool diagnostics)
  {
    var radiusMin = data.m_spawnRadiusMin > 0 ? data.m_spawnRadiusMin : DefaultSpawnRadiusMin;
    var radiusMax = data.m_spawnRadiusMax > 0 ? data.m_spawnRadiusMax : DefaultSpawnRadiusMax;
    List<string>? reasons = diagnostics ? [] : null;
    for (int i = 0; i < MaxSpawnPointTries; i++)
    {
      var dir = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(radiusMin, radiusMax);
      var pos = playerPos + new Vector3(dir.x, 0, dir.y);
      pos.y = GetHeight(pos);
      if (GetBadSpawnPointReason(data, pos, radiusMin, peers) is not { } reason)
        return pos;
      reasons?.Add(reason);
    }
    if (reasons is not null)
      Logger.LogInfo($"{VilePrefabName}: no spawn point found near {playerPos}: {string.Join(", ", reasons.GroupBy(static x => x).Select(static x => $"{x.Key} ({x.Count()}x)"))}");
    return null;
  }

  string? GetBadSpawnPointReason(SpawnSystem.SpawnData data, Vector3 pos, float minPlayerDistance, PeersEnumerable peers)
  {
    if (GetBiome(pos) is var biome && (data.m_biome & biome) is 0)
      return $"wrong biome {biome}";
    if (WorldGenerator.instance.GetBiomeArea(pos) is var area && (data.m_biomeArea & area) is 0)
      return $"wrong biome area {area}";

    var waterLevel = ZoneSystem.instance.m_waterLevel;
    var altitude = pos.y - waterLevel;
    if (altitude < data.m_minAltitude || altitude > data.m_maxAltitude)
      return "altitude";
    if (data.m_minOceanDepth != data.m_maxOceanDepth && (-altitude < data.m_minOceanDepth || -altitude > data.m_maxOceanDepth))
      return "ocean depth";

    var distanceFromCenter = Utils.LengthXZ(pos);
    if (data.m_minDistanceFromCenter > 0 && distanceFromCenter < data.m_minDistanceFromCenter)
      return "too close to world center";
    if (data.m_maxDistanceFromCenter > 0 && distanceFromCenter > data.m_maxDistanceFromCenter)
      return "too far from world center";

    var inForest = WorldGenerator.InForest(pos);
    if ((inForest && !data.m_inForest) || (!inForest && !data.m_outsideForest))
      return inForest ? "in forest" : "outside forest";

    var inLava = GetHeightmap(pos).IsLava(pos, 0.6f);
    if ((inLava && !data.m_inLava) || (!inLava && !data.m_outsideLava))
      return inLava ? "in lava" : "outside lava";

    if (!data.m_canSpawnCloseToPlayer)
    {
      foreach (var peer in peers)
      {
        if (Utils.DistanceXZ(peer.RefPos, pos) < minPlayerDistance)
          return "close to player";
      }
    }

    foreach (var zdo in _sectorObjects)
    {
      var prefab = zdo.GetPrefab();
      if (prefab == __vilePrefab)
      {
        if (data.m_spawnDistance > 0 && Utils.DistanceXZ(zdo.GetPosition(), pos) < data.m_spawnDistance)
          return "close to other Vile";
      }
      else if (!data.m_insidePlayerBase && _noSpawnAreaRadiusByPrefab.TryGetValue(prefab, out var radius) &&
        Utils.DistanceXZ(zdo.GetPosition(), pos) < radius)
      {
        return "inside player base";
      }
    }

    return null;
  }

  void SpawnGroup(SpawnSystem.SpawnData data, Vector3 spawnPoint, int? fixedLevel, bool diagnostics)
  {
    var count = UnityEngine.Random.Range(data.m_groupSizeMin, data.m_groupSizeMax + 1);
    for (int i = 0; i < count; i++)
    {
      var pos = spawnPoint;
      if (i > 0)
      {
        var offset = UnityEngine.Random.insideUnitCircle * data.m_groupRadius;
        pos += new Vector3(offset.x, 0, offset.y);
        pos.y = GetHeight(pos);
      }
      pos.y += data.m_groundOffset + UnityEngine.Random.Range(0f, data.m_groundOffsetRandom);

      int level;
      if (fixedLevel is not null)
        level = fixedLevel.Value;
      else
      {
        level = data.m_minLevel;
        var levelUpChance = SpawnSystem.GetLevelUpChance(pos, data);
        while (level < data.m_maxLevel && UnityEngine.Random.Range(0f, 100f) <= levelUpChance)
          level++;
      }

      var zdo = Spawn(__vilePrefab, pos, Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0));
      if (level > 1)
        zdo.Vars.SetLevel(level);
      if (data.m_huntPlayer)
        zdo.ZDO.Set(ZDOVars.s_huntPlayer, true);
      _sectorObjects.Add(zdo.ZDO);
    }
    if (diagnostics)
      Logger.LogInfo($"Spawned {count} {VilePrefabName} (level {fixedLevel?.ToString() ?? "rolled"}) at {spawnPoint}");
  }
}
