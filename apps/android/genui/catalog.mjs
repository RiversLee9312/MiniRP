import { library as intelligentUi } from "@openuidev/intelligent-ui-library";
import { defineComponent, reactive, useStateField } from "@openuidev/react-lang";
import { createElement, useEffect, useRef, useState } from "react";
import { z } from "zod";

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

export const library = intelligentUi.extend({
    components: { add: [MapView], remove: ["HtmlView", "YouTube"] },
});

export const prompt = library.prompt({
    preamble: "You can compose a native chat reply with Markdown and optional interactive OpenUI Lang surfaces.",
    inlineMode: true,
    bindings: true,
    builtinFunctions: true,
    toolCalls: false,
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
