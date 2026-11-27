import { useRef, useState } from "react";
import { Icon } from "./ui.jsx";

function timeLabel(seconds) {
  const value = Math.floor(Math.max(0, seconds));
  const hours = Math.floor(value / 3600);
  const minutes = Math.floor(value / 60) % 60;
  const rest = String(value % 60).padStart(2, "0");
  return hours ? hours + ":" + String(minutes).padStart(2, "0") + ":" + rest : minutes + ":" + rest;
}

export default function AttachmentAudioPreview({ src, t, onError }) {
  const media = useRef(null);
  const [playback, setPlayback] = useState({ position: 0, duration: 0, playing: false });
  const { position, duration, playing } = playback;
  function update(element) {
    setPlayback({ position: element.currentTime, duration: Number.isFinite(element.duration) ? element.duration : 0,
      playing: !element.paused && !element.ended });
  }
  const changed = (event) => update(event.currentTarget);
  const seekable = duration > 0;
  return <div className="audio-preview">
    <audio ref={media} src={src} preload="metadata" onLoadedMetadata={changed} onDurationChange={changed}
      onTimeUpdate={changed} onPlay={changed} onPause={changed} onEnded={changed} onError={onError} />
    <button type="button" className="filled audio-preview-play" aria-label={t(playing ? "audio_pause" : "audio_play")}
      onClick={() => { const element = media.current; if (element.paused) void element.play().catch(onError); else element.pause(); }}>
      {playing ? <span className="audio-preview-pause" aria-hidden="true" /> : <Icon name="play_arrow" />}
    </button>
    <div className="audio-preview-timeline" dir="ltr">
      <input type="range" aria-label={t("audio_position")} aria-valuetext={timeLabel(position)}
        min="0" max={duration || 1} step="0.01" value={Math.min(position, duration)} disabled={!seekable}
        style={{ "--elapsed": (seekable ? position / duration * 100 : 0) + "%" }}
        onChange={(event) => { media.current.currentTime = Number(event.target.value); update(media.current); }} />
      <div className="audio-preview-times" aria-hidden="true">
        <span>{timeLabel(position)}</span><span>{seekable ? timeLabel(duration) : "–:–"}</span>
      </div>
    </div>
  </div>;
}
