const predictionWindow = 3800;
const trackingTime = 180;

export function retargetRoomProgress(previous, reported, ready, total, now) {
  const stage = 1 / Math.max(1, total);
  // Leave the end of the current character's segment for its readiness call.
  const limit = ready === total ? 1 : (ready + .96) * stage;
  const confirmed = Math.min(limit, Math.max(ready * stage, reported));
  const room = Math.max(0, limit - confirmed);
  const rate = previous ? Math.max(0, confirmed - previous.confirmed) / Math.max(400, now - previous.since) : 0;
  const runway = Math.min(room * .54, Math.max(room * .24, rate * predictionWindow));
  return { value: previous?.value ?? ready * stage, confirmed, runway, since: now };
}

export function advanceRoomProgress(target, now, elapsed) {
  const estimate = target.confirmed - target.runway * Math.expm1(-(now - target.since) / predictionWindow);
  const step = -Math.expm1(-elapsed / trackingTime);
  return target.value + Math.max(0, estimate - target.value) * step;
}
