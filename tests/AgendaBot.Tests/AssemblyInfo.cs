// Las dos fábricas comparten la misma base cuando se usa AGENDABOT_TEST_SQL, así que las
// colecciones no pueden correr en paralelo.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
