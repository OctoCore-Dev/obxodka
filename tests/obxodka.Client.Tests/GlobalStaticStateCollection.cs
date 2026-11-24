namespace obxodka.Client.Tests;

//коллекция для изоляции тестов, изменяющих глобальные статические поля (AppConfig, OctopusEngine)
[CollectionDefinition("GlobalStaticState", DisableParallelization = true)]
public sealed class GlobalStaticStateCollection;
