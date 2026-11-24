import { useEffect, useState } from "react";
import { send } from "./api.js";
import { MessageBlock } from "./Conversation.jsx";
import { Icon, Modal } from "./ui.jsx";
import "./EnsembleRoom.css";

function Avatar({ name }) {
  const first = new Intl.Segmenter().segment(name?.trim() || "").containing(0)?.segment;
  return <span className="ensemble-avatar" aria-hidden="true">{first || <Icon name="chat_bubble" />}</span>;
}

export function PrivateMessagesFab({ extended, disabled, buttonRef, open, t }) {
  const label = t("ensemble_private_messages");
  return <button className="room-private-fab" type="button" ref={buttonRef} disabled={disabled}
    data-extended={extended || undefined} title={label} aria-label={label} aria-haspopup="dialog" onClick={open}>
    <Icon name="chat_bubble" />
    {extended && <span>{label}</span>}
  </button>;
}

export function RoomThinking({ people, t }) {
  return <div className="room-thinking" role="status" aria-live="polite">
    {people.length > 0 && <ul>
      {people.map(({ participant, speaker }) => <li key={participant} dir="auto">
        <span className="room-thinking-dot" aria-hidden="true" />
        {t("ensemble_member_thinking", "\u2068" + speaker + "\u2069")}
      </li>)}
    </ul>}
  </div>;
}

export function EnsembleOverview({ scenes, activity, open, disabled, t }) {
  return <section className="ensemble-overview" aria-label={t("conversations")}>
    <ul className="ensemble-scene-list">
      {scenes.map((scene) => <li key={scene.id}>
        <button type="button" className="room-select ensemble-scene" disabled={disabled} onClick={() => open(scene.id)}>
          <span className="ensemble-scene-icon"><Icon name="chat_bubble" /></span>
          <span className="ensemble-scene-text">
            <strong><bdi>{scene.name || t("chat")}</bdi></strong>
            {scene.members?.length > 0 && <small>{scene.members.map((name, index) =>
              <bdi key={index}>{index > 0 && <span aria-hidden="true"> · </span>}{name}</bdi>)}</small>}
          </span>
          <span className="directional"><Icon name="chevron_right" /></span>
        </button>
        <RoomThinking people={Object.values(activity[scene.id] || {})} t={t} />
      </li>)}
    </ul>
  </section>;
}

export function OutOfStoryNotice({ t }) {
  return <p className="ensemble-ooc">
    <Icon name="info" /><span>{t("ensemble_out_of_story")}</span>
  </p>;
}

export function EnsembleRoster({ people, inStory, t, close, open }) {
  const [opening, setOpening] = useState("");
  async function choose(person) {
    setOpening(person.id);
    try {
      if (await open(person.id)) close(false);
    } finally {
      setOpening("");
    }
  }
  return <Modal title={t("ensemble_private_messages")} close={() => close(true)} closeDisabled={Boolean(opening)}
    t={t} className="ensemble-roster-modal">
    {!inStory && <OutOfStoryNotice t={t} />}
    <ul className="ensemble-contact-list">
      {people.map((person) => <li key={person.id}>
        <button type="button" className="room-select ensemble-contact" disabled={Boolean(opening)}
          aria-busy={opening === person.id || undefined} onClick={() => choose(person)}>
          <Avatar name={person.name} />
          <bdi className="choice-label">{person.name || t("chat")}</bdi>
          <span className="directional"><Icon name="chevron_right" /></span>
        </button>
      </li>)}
    </ul>
  </Modal>;
}

export function EnsembleTraceDialog({ participant, active, scope, locale, dark, theme, expanded, t, close }) {
  const { preset, room } = scope;
  const [blocks, setBlocks] = useState(null);
  const [error, setError] = useState(false);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    let timer;
    async function load() {
      try {
        const trace = await send("ensemble_member_trace", { preset, chat: room, participant, locale }, controller.signal);
        if (controller.signal.aborted) return;
        if (!Array.isArray(trace.blocks)) throw new Error();
        setError(false);
        setBlocks(trace.blocks);
        if (active) timer = setTimeout(load, 1500);
      } catch {
        if (!controller.signal.aborted) setError(true);
      }
    }
    setError(false);
    load();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [active, locale, participant, preset, retry, room]);
  return <Modal title={t("ensemble_member_trace")} close={close} t={t} wide className="ensemble-trace-modal">
    {error
      ? <div className="ensemble-trace-empty" role="status">
        <p>{t("ensemble_trace_load_failed")}</p>
        <button className="text-button" type="button" onClick={() => setRetry(retry + 1)}>{t("retry")}</button>
      </div>
      : !blocks ? <p className="ensemble-trace-empty" role="status">{t("running")}</p>
        : blocks.length === 0 ? <p className="ensemble-trace-empty" role="status">{t("ensemble_no_trace")}</p> : null}
    {blocks?.length > 0 && <div className="ensemble-trace-list">
      {blocks.map((block, index) => <section key={block.conversation + ":" + block.id}>
        {block.scene_name && (index === 0 || block.conversation !== blocks[index - 1].conversation) &&
          <h3 dir="auto" className="ensemble-trace-scene">{block.scene_name}</h3>}
        <MessageBlock block={block} scope={{ ...scope, conversation: block.conversation || scope.conversation }}
          {...{ locale, dark, theme, expanded, t }} nested />
      </section>)}
    </div>}
  </Modal>;
}
