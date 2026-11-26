import assert from "node:assert/strict";
import test from "node:test";
import { advanceRoomProgress, retargetRoomProgress } from "./roomMotion.js";

function tick(target, from, to) {
  for (let time = from + 20; time <= to; time += 20) {
    target.value = advanceRoomProgress(target, time, 20);
  }
  return target.value;
}

test("room progress predicts ahead and slows between reports", () => {
  const progress = retargetRoomProgress(null, .20, 0, 2, 0);
  const first = tick(progress, 0, 3000);
  const second = tick(progress, 3000, 6000);
  const third = tick(progress, 6000, 9000);
  assert.ok(first > .20);
  assert.ok(third > second);
  assert.ok(third - second < second - first);
  assert.ok(third < .5);
});

test("reported progress catches up smoothly and waits for readiness to finish", () => {
  const progress = retargetRoomProgress(null, .2, 0, 2, 0);
  const before = tick(progress, 0, 3000);
  const reported = retargetRoomProgress(progress, 1, 1, 2, 3000);
  assert.equal(reported.value, before);
  const firstFrame = tick(reported, 3000, 3020);
  assert.ok(firstFrame > before && firstFrame < .98);
  assert.ok(tick(reported, 3020, 10000) < 1);
  const ready = retargetRoomProgress(reported, 1, 2, 2, 10000);
  assert.equal(ready.confirmed, 1);
  assert.ok(tick(ready, 10000, 12000) > .999);
});

test("plugin weights determine reported progress even when most characters are ready", () => {
  const target = retargetRoomProgress(null, 7 / 17, 1, 2, 0, true);
  assert.equal(target.confirmed, 7 / 17);
  assert.equal(tick(target, 0, 10000), 7 / 17);
});
