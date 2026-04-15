import { loadEvents, loadMetadata } from "../parser/loader";
import type { AttributionRef, CombatEvent } from "../types/events";

type CheckResult = {
  id: string;
  ok: boolean;
  detail: string;
};

function triggerKind(ref: AttributionRef | undefined): string | undefined {
  return ref?.kind;
}

function findBlockChanged(
  events: CombatEvent[],
  predicate: (event: Extract<CombatEvent, { event_type: "block_changed" }>) => boolean,
): Extract<CombatEvent, { event_type: "block_changed" }> | undefined {
  return events.find(
    (event): event is Extract<CombatEvent, { event_type: "block_changed" }> =>
      event.event_type === "block_changed" && predicate(event),
  );
}

function findEvent<T extends CombatEvent["event_type"]>(
  events: CombatEvent[],
  eventType: T,
  predicate: (event: Extract<CombatEvent, { event_type: T }>) => boolean = () => true,
): Extract<CombatEvent, { event_type: T }> | undefined {
  return events.find(
    (event): event is Extract<CombatEvent, { event_type: T }> =>
      event.event_type === eventType && predicate(event as Extract<CombatEvent, { event_type: T }>),
  );
}

async function auditBattleDir(battleDir: string): Promise<CheckResult[]> {
  const [metadata, events] = await Promise.all([
    loadMetadata(battleDir),
    loadEvents(battleDir),
  ]);

  const results: CheckResult[] = [];

  const loseBlock = findBlockChanged(
    events,
    (event) => event.payload.reason === "block_loss" && event.payload.delta < 0,
  );
  const blockBroken = findEvent(events, "block_broken");
  results.push({
    id: "block-explicit-loss",
    ok: !!loseBlock && !!blockBroken,
    detail: loseBlock && blockBroken
      ? `resolution=${loseBlock.resolution_id ?? "none"} old=${loseBlock.payload.old} new=${loseBlock.payload.new}`
      : "missing explicit block_loss/block_broken pair",
  });

  const blockClear = findBlockChanged(
    events,
    (event) => event.payload.reason === "block_clear",
  );
  const blockCleared = findEvent(events, "block_cleared");
  results.push({
    id: "block-clear",
    ok: !!blockClear && !!blockCleared,
    detail: blockClear && blockCleared
      ? `old=${blockClear.payload.old} new=${blockClear.payload.new}`
      : "missing block_clear/block_cleared pair",
  });

  const preventedByPower = findEvent(
    events,
    "block_clear_prevented",
    (event) => triggerKind(event.payload.preventer) === "power_instance",
  );
  results.push({
    id: "block-clear-prevented-power",
    ok: !!preventedByPower,
    detail: preventedByPower
      ? `retained=${preventedByPower.payload.retained_block} preventer=${preventedByPower.payload.preventer.kind}`
      : "missing power-rooted block_clear_prevented",
  });

  const preventedByRelic = findEvent(
    events,
    "block_clear_prevented",
    (event) => triggerKind(event.payload.preventer) === "relic",
  );
  const relicTrim = findBlockChanged(
    events,
    (event) => event.payload.reason === "block_loss" && triggerKind(event.payload.trigger) === "relic",
  );
  results.push({
    id: "block-clear-prevented-relic-trim",
    ok: !!preventedByRelic && !!relicTrim,
    detail: preventedByRelic && relicTrim
      ? `retained=${preventedByRelic.payload.retained_block} trimmed_to=${relicTrim.payload.new}`
      : "missing relic prevented-clear with follow-up trim",
  });

  const afterClearedGain = findBlockChanged(
    events,
    (event) =>
      event.payload.reason === "block_gain" &&
      triggerKind(event.payload.trigger) === "power_instance",
  );
  results.push({
    id: "block-after-cleared-family",
    ok: !!blockCleared && !!afterClearedGain,
    detail: blockCleared && afterClearedGain
      ? `gain_trigger=${afterClearedGain.payload.trigger?.kind}`
      : "missing AfterBlockCleared follow-up block gain",
  });

  const cardEnteredRelicGain = findBlockChanged(
    events,
    (event) =>
      event.payload.reason === "block_gain" &&
      triggerKind(event.payload.trigger) === "relic",
  );
  const enteredCard = findEvent(events, "card_created");
  results.push({
    id: "block-after-card-entered-family",
    ok: !!enteredCard && !!cardEnteredRelicGain,
    detail: enteredCard && cardEnteredRelicGain
      ? `resolution=${enteredCard.resolution_id ?? "none"}`
      : "missing card-entered follow-up block gain",
  });

  const cardExhausted = findEvent(events, "card_exhausted");
  const moveToExhaust = findEvent(
    events,
    "card_moved",
    (event) => event.payload.to_zone === "exhaust",
  );
  results.push({
    id: "card-exhausted",
    ok: !!cardExhausted && !!moveToExhaust,
    detail: cardExhausted && moveToExhaust
      ? `card=${cardExhausted.payload.card_instance_id}`
      : "missing explicit card_exhausted lifecycle",
  });

  const rootlessCardCreated = findEvent(
    events,
    "card_created",
    (event) => !event.resolution_id && !event.payload.trigger,
  );
  results.push({
    id: "card-rootless-generated-boundary",
    ok: !!rootlessCardCreated,
    detail: rootlessCardCreated
      ? `card=${rootlessCardCreated.payload.card_instance_id}`
      : "missing rootless blank-trigger card_created",
  });

  const entityRevived = findEvent(events, "entity_revived");
  results.push({
    id: "entity-revive",
    ok: !!entityRevived,
    detail: entityRevived
      ? `entity=${entityRevived.payload.entity_id} trigger=${triggerKind(entityRevived.payload.trigger) ?? "blank"}`
      : "missing entity_revived",
  });

  const energyCardSpend = findEvent(
    events,
    "energy_changed",
    (event) => event.payload.reason === "card_play" && triggerKind(event.payload.trigger) === "card_instance",
  );
  results.push({
    id: "resource-energy-card",
    ok: !!energyCardSpend,
    detail: energyCardSpend
      ? `old=${energyCardSpend.payload.old} new=${energyCardSpend.payload.new}`
      : "missing card-rooted energy_changed",
  });

  const starsPotionGain = findEvent(
    events,
    "resource_changed",
    (event) =>
      event.payload.resource_id === "stars" &&
      triggerKind(event.payload.trigger) === "potion_instance",
  );
  results.push({
    id: "resource-stars-potion",
    ok: !!starsPotionGain,
    detail: starsPotionGain
      ? `old=${starsPotionGain.payload.old} new=${starsPotionGain.payload.new}`
      : "missing potion-rooted stars gain",
  });

  const starsPowerGain = findEvent(
    events,
    "resource_changed",
    (event) =>
      event.payload.resource_id === "stars" &&
      triggerKind(event.payload.trigger) === "power_instance",
  );
  results.push({
    id: "resource-stars-power",
    ok: !!starsPowerGain,
    detail: starsPowerGain
      ? `old=${starsPowerGain.payload.old} new=${starsPowerGain.payload.new}`
      : "missing power-rooted stars gain",
  });

  const orbInserted = findEvent(
    events,
    "orb_inserted",
    (event) => event.payload.reason === "channel",
  );
  results.push({
    id: "orb-channel",
    ok: !!orbInserted,
    detail: orbInserted
      ? `orb=${orbInserted.payload.orb_id} slot=${orbInserted.payload.slot_index}`
      : "missing orb_inserted(channel)",
  });

  const orbPassiveAfterTurnStart = findEvent(
    events,
    "orb_passive_triggered",
    (event) => event.payload.timing === "after_turn_start",
  );
  const orbEnergy = findEvent(
    events,
    "energy_changed",
    (event) => triggerKind(event.payload.trigger) === "orb_instance",
  );
  results.push({
    id: "orb-passive-after-turn-start",
    ok: !!orbPassiveAfterTurnStart && !!orbEnergy,
    detail: orbPassiveAfterTurnStart && orbEnergy
      ? `orb=${orbPassiveAfterTurnStart.payload.orb_id} energy=${orbEnergy.payload.old}->${orbEnergy.payload.new}`
      : "missing after-turn-start orb passive energy lane",
  });

  const orbEvoked = findEvent(events, "orb_evoked");
  const orbEvokeDamage = findEvent(
    events,
    "damage_attempt",
    (event) => triggerKind(event.payload.executor) === "orb_instance",
  );
  results.push({
    id: "orb-evoke-damage",
    ok: !!orbEvoked && !!orbEvokeDamage,
    detail: orbEvoked && orbEvokeDamage
      ? `orb=${orbEvoked.payload.orb_id} target=${orbEvokeDamage.payload.settled_target_entity_id}`
      : "missing orb evoke + orb-instance damage attempt",
  });

  const orbModifiedByPower = findEvent(
    events,
    "orb_modified",
    (event) => event.payload.reason === "power_changed" && triggerKind(event.payload.trigger) === "power_instance",
  );
  results.push({
    id: "orb-modified-power",
    ok: !!orbModifiedByPower,
    detail: orbModifiedByPower
      ? `orb=${orbModifiedByPower.payload.orb_id} trigger=${orbModifiedByPower.payload.trigger?.kind}`
      : "missing power-rooted orb_modified",
  });

  const orbModifiedInternally = findEvent(
    events,
    "orb_modified",
    (event) => event.payload.reason === "internal_state_changed" && triggerKind(event.payload.trigger) === "orb_instance",
  );
  results.push({
    id: "orb-modified-internal",
    ok: !!orbModifiedInternally,
    detail: orbModifiedInternally
      ? `orb=${orbModifiedInternally.payload.orb_id}`
      : "missing orb-instance internal orb_modified",
  });

  process.stderr.write(
    `Audit target: ${metadata.battle_id} (${metadata.battle?.encounter_name ?? metadata.battle?.encounter_id ?? "unknown"})\n`,
  );

  return results;
}

async function main(): Promise<void> {
  const battleDir = process.argv[2];
  if (!battleDir) {
    process.stderr.write("Usage: bun run src/inspector/truthAudit.ts <battle-dir>\n");
    process.exit(1);
  }

  const results = await auditBattleDir(battleDir);
  for (const result of results) {
    process.stdout.write(`${result.ok ? "PASS" : "MISS"} ${result.id} ${result.detail}\n`);
  }

  if (results.some((result) => !result.ok)) {
    process.exitCode = 2;
  }
}

main().catch((err: unknown) => {
  const message = err instanceof Error ? err.stack ?? err.message : String(err);
  process.stderr.write(`Error: ${message}\n`);
  process.exit(1);
});
