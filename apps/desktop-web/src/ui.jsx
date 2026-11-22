import { useEffect, useLayoutEffect, useRef, useState } from "react";

export function Icon({ name }) {
  return <span className="symbol" aria-hidden="true">{name}</span>;
}

export function IconButton({ icon, label, className = "", ...props }) {
  return <button type="button" className={"icon-button " + className} aria-label={label} title={label} {...props}>
    <Icon name={icon} />
  </button>;
}

export function SettingToggle({ label, detail, checked, onChange, disabled }) {
  return <label className="settings-row settings-toggle">
    <span className="setting-label">
      <span>{label}</span>
      {detail && <small>{detail}</small>}
    </span>
    <span className="setting-switch">
      <input type="checkbox" role="switch" aria-label={label} checked={checked} disabled={disabled}
        onChange={(event) => onChange(event.target.checked)} />
      <span aria-hidden="true" />
    </span>
  </label>;
}

export function Notice({ message, close, t }) {
  const popup = useRef(null);
  useLayoutEffect(() => {
    const element = popup.current;
    element.showPopover();
    return () => element.hidePopover();
  }, []);
  return <div className="notice" role="alert" ref={popup} popover="manual">
    <span>{message}</span><IconButton icon="close" label={t("close")} onClick={close} />
  </div>;
}

export function Modal({ title, close, back, children, t, wide = false, heading = true, className = "", closeDisabled = false }) {
  const ref = useRef(null);
  useEffect(() => {
    const dialog = ref.current;
    dialog.showModal();
    return () => dialog.close();
  }, []);
  return <dialog ref={ref} className={["modal", wide ? "wide" : "", className].filter(Boolean).join(" ")} aria-label={title}
    onCancel={(event) => { event.preventDefault(); if (back || !closeDisabled) (back || close)(); }}>
    {heading && <header className="dialog-header">
      {back && <IconButton className="directional dialog-back" icon="arrow_back" label={t("back")} onClick={back} />}
      <h2>{title}</h2>
      <IconButton icon="close" label={t("close")} disabled={closeDisabled} onClick={close} />
    </header>}
    <div className="dialog-content">{children}</div>
  </dialog>;
}

export function TextDialog({ dialog, close, t, run }) {
  const [value, setValue] = useState(dialog.value || "");
  const [pending, setPending] = useState(false);
  const label = t(dialog.label || "chat_name");
  const submit = (action) => {
    if (pending) return;
    setPending(true);
    run(async () => {
      try { await action(); close(); }
      finally { setPending(false); }
    });
  };
  return <Modal title={dialog.title} close={close} closeDisabled={pending} t={t}>
    <form className="stack" onSubmit={(event) => {
      event.preventDefault();
      submit(() => dialog.submit(value.trim()));
    }}>
      <label>{label}
        {dialog.multiline
          ? <textarea rows="8" value={value} readOnly={pending} onChange={(event) => setValue(event.target.value)} />
          : <input required value={value} readOnly={pending} maxLength="180" onChange={(event) => setValue(event.target.value)} />}
      </label>
      <div className="actions">
        {dialog.secondary && <button type="button" className="text-button dialog-secondary" disabled={pending}
          onClick={() => submit(dialog.secondary.submit)}>
          {dialog.secondary.label}
        </button>}
        <button type="button" className="text-button" disabled={pending} onClick={close}>{t("cancel")}</button>
        <button className="filled" type="submit" disabled={pending || !dialog.multiline && !value.trim()}>
          {t(pending ? "running" : dialog.submitLabel || "save")}
        </button>
      </div>
    </form>
  </Modal>;
}

export function Confirm({ dialog, close, t, run }) {
  return <Modal title={dialog.title} close={close} t={t}>
    {dialog.message && <p>{dialog.message}</p>}
    <div className="actions">
      <button type="button" className="text-button" onClick={close}>{t("cancel")}</button>
      <button type="button" className={"filled" + (dialog.danger || !dialog.label || dialog.label === t("delete") ? " danger-button" : "")}
        onClick={() => run(async () => { await dialog.submit(); close(); })}>
        {dialog.label || t("delete")}
      </button>
    </div>
  </Modal>;
}

export function FileChoice({ label, choose, accept, multiple = false, disabled, icon, iconOnly = false, className = "outlined" }) {
  const file = useRef(null);
  return <>
    <button type="button" className={className} disabled={disabled}
      aria-label={iconOnly ? label : undefined} title={iconOnly ? label : undefined} onClick={() => file.current.click()}>
      {icon && <Icon name={icon} />}{!iconOnly && label}
    </button>
    <input type="file" ref={file} hidden accept={accept} multiple={multiple} onChange={(event) => {
      if (event.target.files.length) choose(Array.from(event.target.files));
      event.target.value = "";
    }} />
  </>;
}
