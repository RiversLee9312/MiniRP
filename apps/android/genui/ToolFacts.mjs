import { defineComponent } from "@openuidev/react-lang";
import { createElement as h } from "react";
import { z } from "zod";

const row = z.object({ label: z.string(), value: z.string(), mono: z.boolean().optional() });

function FactValue({ value, mono, more, less }) {
    const style = "tool-fact-value" + (mono ? " tool-fact-code" : "");
    const lines = value.split("\n");
    if (value.length <= 180 && lines.length <= 4) return h("div", { className: style, dir: "auto" }, value);
    const excerpt = Array.from(lines.slice(0, 3).join("\n")).slice(0, 132).join("").trimEnd() + "…";
    return h("details", { className: "tool-fact-details" },
        h("summary", { className: "tool-fact-toggle" },
            h("span", { className: "tool-fact-more" }, more), h("span", { className: "tool-fact-less" }, less),
            h("span", { className: style + " tool-fact-preview", dir: "auto" }, excerpt)),
        h("div", { className: style + " tool-fact-full", dir: "auto" }, value));
}

export const ToolFacts = defineComponent({
    name: "ToolFacts",
    description: "Display read-only tool details as labeled, selectable fields. Long values expand locally.",
    props: z.object({
        rows: z.array(row).max(18), status: z.string(), note: z.string(), more: z.string(), less: z.string(),
    }),
    component: ({ props: { rows, status, note, more, less } }) => h("section", { className: "tool-facts" },
        status && h("p", { className: "tool-facts-status", dir: "auto" }, status),
        rows.length > 0 && h("dl", { className: "tool-facts-list" }, rows.map(({ label, value, mono }, index) =>
            h("div", { key: index, className: "tool-fact-row" }, h("dt", { dir: "auto" }, label),
                h("dd", null, h(FactValue, { value, mono, more, less }))))),
        note && h("p", { className: "tool-facts-note", dir: "auto" }, note)),
});
