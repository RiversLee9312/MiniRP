import { library as intelligentUi } from "@openuidev/intelligent-ui-library";
import { defineComponent, reactive, useStateField } from "@openuidev/react-lang";
import { createContext, createElement, useContext, useEffect, useRef, useState } from "react";
import { z } from "zod";
import { ToolFacts } from "./ToolFacts.mjs";

const place = z.object({
    label: z.string(),
    lat: z.number().min(-85).max(85),
    lng: z.number().min(-180).max(180),
});

const MapView = defineComponent({
    name: "Map",
    description: "A street map of named places in order. Pass true to connect the pins with an overview line. Bind selected to a variable to react to pin taps.",
    props: z.object({
        places: z.array(place).max(24),
        connectPins: z.boolean().optional(),
        selected: reactive(z.string().optional()),
    }),
    component: MapComponent,
});

function MapComponent({ props, statementId }) {
    const container = useRef(null);
    const selection = useStateField(statementId ?? "map", props.selected);
    const select = useRef(selection.setValue);
    select.current = selection.setValue;
    const [failed, setFailed] = useState(false);
    const pins = JSON.stringify(props.places ?? []);

    useEffect(() => {
        const places = JSON.parse(pins);
        const target = container.current;
        if (!target || places.length === 0) return;
        let mounted = true;
        let map;
        setFailed(false);
        import("maplibre-gl").then(({ Map, Marker, LngLatBounds, NavigationControl }) => {
            if (!mounted) return;
            const points = places.map(({ lng, lat }) => [lng, lat]);
            map = new Map({
                container: target,
                style: "https://tiles.openfreemap.org/styles/liberty",
                center: points[0],
                zoom: 13,
                renderWorldCopies: false,
                cooperativeGestures: true,
                dragRotate: false,
                pitchWithRotate: false,
            });
            map.addControl(new NavigationControl({ showCompass: false }));
            if (points.length > 1) {
                const bounds = new LngLatBounds(points[0], points[0]);
                for (const point of points) bounds.extend(point);
                map.fitBounds(bounds, { padding: 38, maxZoom: 15, duration: 0 });
                if (props.connectPins) map.once("load", () => {
                    map.addSource("overview", {
                        type: "geojson",
                        data: { type: "Feature", properties: {}, geometry: { type: "LineString", coordinates: points } },
                    });
                    map.addLayer({
                        id: "overview", type: "line", source: "overview",
                        paint: { "line-color": "#5367bc", "line-width": 3, "line-dasharray": [2, 2] },
                    });
                });
            }
            places.forEach(({ label }, index) => {
                const pin = document.createElement("button");
                pin.type = "button";
                pin.className = "map-pin";
                pin.textContent = String(index + 1);
                pin.setAttribute("aria-label", label);
                pin.addEventListener("click", () => select.current(label));
                new Marker({ element: pin }).setLngLat(points[index]).addTo(map);
            });
        }).catch(() => { if (mounted) setFailed(true); });
        return () => {
            mounted = false;
            map?.remove();
        };
    }, [pins, props.connectPins]);

    return createElement("section", { className: "map-card" },
        failed ? null : createElement("div", { className: "map", ref: container }),
        createElement("ol", { className: "map-places" }, (props.places ?? []).map((item, index) =>
            createElement("li", { key: index },
                createElement("button", {
                    type: "button",
                    "aria-pressed": selection.value === item.label,
                    onClick: () => selection.setValue(item.label),
                }, item.label)))),
    );
}

export const AssetMedia = createContext(false);
const assetUrl = /^minirp:\/\/assets\/[a-z0-9._]+\/[A-Za-z0-9_.-]+$/;
const resourcePath = "/asset-resource/";

export function originalAssetUrl(url) {
    const resource = new URL(url, document.baseURI);
    if (resource.origin !== location.origin) return url;
    if (resource.pathname === "/api/asset") return resource.searchParams.get("url") ?? url;
    return resource.pathname.startsWith(resourcePath) ? decodeURIComponent(resource.pathname.slice(resourcePath.length)) : url;
}

function hostAssetUrl(url, ready) {
    if (!assetUrl.test(url)) return url;
    if (!ready) return "";
    return window.MiniRPSurface?.assetUrl?.(url) ?? location.origin + resourcePath + encodeURIComponent(url);
}

function assetComponent(name, property, convert, description) {
    const original = intelligentUi.components[name];
    return defineComponent({
        ...original,
        description: description ?? original.description,
        component: (input) => {
            const ready = useContext(AssetMedia);
            return createElement(original.component, {
                ...input,
                props: { ...input.props, [property]: convert(input.props[property], ready) },
            });
        },
    });
}

export const library = intelligentUi.extend({
    components: {
        add: [MapView],
        remove: ["HtmlView", "YouTube"],
        override: [
            assetComponent("Image", "src", hostAssetUrl, "Photo from an HTTPS URL or an Asset URL supplied to the session. aspectRatio is 0.2 to 5 (default 1.5)."),
            assetComponent("Link", "url", hostAssetUrl, "Opens an HTTPS link or downloads a shared Asset URL."),
            assetComponent("Markdown", "text", (text, ready) => (text ?? "").replace(
                /\]\((minirp:\/\/assets\/[a-z0-9._]+\/[A-Za-z0-9_.-]+)\)/g,
                (_, url) => "](" + hostAssetUrl(url, ready) + ")",
            )),
        ],
    },
});

const toolComponents = new Set(["Col", "Row", "Text", "Title", "Card", "Divider", "Table", "Chart", "Badge"]);
export const toolLibrary = library.extend({
    components: { add: [ToolFacts], remove: Object.keys(library.components).filter((name) => !toolComponents.has(name)) },
});

const promptOptions = {
    inlineMode: true,
    bindings: true,
    builtinFunctions: true,
    toolCalls: false,
};

export const prompt = library.prompt({
    ...promptOptions,
    preamble: "You can compose a native chat reply with Markdown and optional interactive OpenUI Lang surfaces.",
    additionalRules: [
        "Put each OpenUI program in a separate unindented fenced block starting with three backticks and openui-lang, and ending with three backticks. Start the program on the next line. Put ordinary prose outside the fences.",
        "Use the user's language and build the smallest useful interactive response. Choose charts and tables for data, drawings for spatial explanations, and forms or local controls for decisions.",
        "For calculators, checklists and configurable dashboards, bind controls to $variables and compute or filter the view locally. Always provide an explicit action for each button. Use @ToAssistant only when the user needs another agent response. Label any button that sends a message accordingly.",
        "For a Form that uses $variables, pass the fields the user is submitting explicitly in @ToAssistant's context, for example @ToAssistant(\"Compare my plan\", {plan: $plan}). Do not include other forms or unrelated interface state.",
        "Use a Map only with coordinates you have verified or received from the user. The optional line is an overview, not turn-by-turn directions. Only use real image URLs supplied by the user or verified by a tool.",
        "Keep each program self-contained. Rank or summarize large datasets before embedding them. Do not repeat an entire generated surface as Markdown.",
    ],
    examples: [
        'root = Col([Title("Trip budget", "lg"), Slider($days, 1, 14, 1, "Days"), Text("Estimated cost: " + @Format($days * 125, "currency", 0, "USD"))])\n$days = 3',
        'root = Col([Title("Compare options", "lg"), Chart("bar", data, "item", ["value"]), Table(["Option", "Value"], @Each(data, "row", [row.item, row.value + ""]))])\ndata = [{item: "A", value: 15}, {item: "B", value: 23}]',
        'root = Col([Title("Choose a plan", "lg"), details])\ndetails = Form([SegmentedControl($plan, ["Basic", "Plus"]), Button("Compare this plan", @ToAssistant("Compare my plan", {plan: $plan}))])\n$plan = "Basic"',
        'root = Col([Title("Route overview", "lg"), Map([{label: "Golden Gate Bridge", lat: 37.8199, lng: -122.4783}, {label: "Ferry Building", lat: 37.7955, lng: -122.3937}], true, $stop), Text($stop)])\n$stop = "Golden Gate Bridge"',
    ],
});

export const cardPrompt = library.prompt({
    ...promptOptions,
    preamble: "Use OpenUI Lang in the program field of a plugin manifest card.",
    additionalRules: [
        "Write the program as plain OpenUI Lang, without Markdown fences. Start with a root component.",
        "If the card binds a state entry, each field in that entry is available as a $variable. Use @Set and @Reset for local state. Durable changes go through plugin Actions.",
        "Use @ToAssistant to send the user's choice and explicit context to the conversation agent. Give each interactive control a unique readable label.",
        "Preview the declared card, compare the rendered output with the fixture, inspect diagnostics and replay important interactions before publishing.",
    ],
    examples: [
        'root = Col([Title("Trip budget", "lg"), Text("Estimated cost: " + ($days * 125)), Button("Add a day", @Set($days, $days + 1)), Button("Use this budget", @ToAssistant("Plan my trip", {days: $days}))])\n$days = 3',
    ],
});
