async function check(response) {
  if (response.ok) return response;
  const error = await response.json().catch(() => ({}));
  throw new Error(error.error || error.message || response.statusText);
}

export async function request(path, body) {
  const response = await check(await fetch(path, body === undefined ? undefined : {
    method: "POST",
    headers: body instanceof FormData ? undefined : { "Content-Type": "application/json" },
    body: body instanceof FormData ? body : JSON.stringify(body),
  }));
  return response.json();
}

export const send = (op, parameters = {}) => request("/api/command", { ...parameters, op });

export function upload(path, files) {
  const form = new FormData();
  for (const file of files) form.append("files", file);
  return request(path, form);
}

export async function download(data) {
  const response = await check(await fetch("/api/export", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(data),
  }));
  const name = response.headers.get("content-disposition")?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement("a");
  link.href = url;
  link.download = name ? decodeURIComponent(name) : data.name || "MiniRP";
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function assetUrl(url, scope = {}, options = {}) {
  return "/api/asset?" + new URLSearchParams({ url, ...scope, ...options });
}

export function attachmentUrl(file) {
  return "/api/attachment/" + encodeURIComponent(file.id) + "?" + new URLSearchParams({ name: file.name });
}
