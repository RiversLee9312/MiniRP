export type Json = null | boolean | number | string | Json[] | { [name: string]: Json };
export type JsonObject = { [name: string]: Json };
export type PluginUrlScheme = "minirp" | "minichat";
export type HookUrl = `${PluginUrlScheme}://actions/${string}/hooks/${string}`;
export type EventUrl = `${PluginUrlScheme}://actions/${string}/events/${string}`;
export type AssetUrl = `${PluginUrlScheme}://assets/${string}/${string}`;
/** Created by the host when a file is shared with an Agent. Use the URL supplied by the host. */
export type WorkspaceAssetUrl = `minirp://assets/minirp.attachments/${string}`;
export type AssetToolResult = AssetUrl | readonly AssetUrl[];
export type UUID = `${string}-${string}-${string}-${string}-${string}`;
export interface PluginManifestIdentity {
    id: string;
    packageName: string;
    /** Canonical lowercase UUID of the owning preset. */
    presetId?: UUID;
}
export type ConversationAgent = "chat" | "env" | "preset";
export type SessionAgent = ConversationAgent | "search" | "fetch" | "temporary";
export interface SkillToolDeclaration {
    name: string;
    description: string;
    parameters: JsonObject & { type: "object" };
    handler: HookUrl;
    returnType?: "json" | "asset-url";
}
export interface SkillDeclaration {
    name: string;
    description: string;
    agents: readonly SessionAgent[];
    instructions?: string;
    tools?: readonly SkillToolDeclaration[];
    hooks?: Readonly<Record<string, HookUrl>>;
}
export type Scope = "session" | "room" | "chat" | "preset";
export type RoomTarget = { preset?: UUID; room?: UUID };
export type ReadTarget = RoomTarget & { chat?: UUID };
export type ChatTarget = ReadTarget & { agent?: ConversationAgent };
export type Page = { offset?: number; limit?: number };

export interface Environment {
    readonly session?: string | null;
    readonly chat?: UUID | null;
    readonly room?: UUID | null;
    readonly participant?: string | null;
    readonly preset?: UUID | null;
    readonly agent?: SessionAgent | "title" | null;
}

export interface SelectedFile {
    id: string;
    name: string;
    mime: string;
}

export interface Message {
    role: "user" | "assistant" | "system";
    agent: string;
    text: string;
    participant?: string | null;
    speaker?: string | null;
}

export interface Queued { queued: true; request: string }
export interface Accepted { accepted: true }
export interface AssetBody {
    readonly mimeType: string;
    readonly length: number | null;
    readonly body: ReadableStream<Uint8Array>;
}
export interface AssetBytes { readonly mimeType: string; readonly bytes: Uint8Array }
export interface AssetMetadata {
    readonly url: AssetUrl;
    readonly package: string;
    readonly id: string;
    readonly source: "workspace_file" | "plugin";
    readonly mime: string;
    readonly length: number | null;
    readonly name: string | null;
}
export type TextEncoding = "utf-8" | "utf-16le" | "utf-16be";
export type TextPage = { text: string; offset: number; next_offset: number | null };
export type MediaInterval = { start_ms?: number; duration_ms?: number };
/** Top-left source coordinates. Use pixels for images and video frames and points for PDF pages. */
export type ImageViewport = { x: number; y: number; width: number; height: number };
export interface RenderedImage extends AssetBody {
    readonly width: number;
    readonly height: number;
    readonly source_width: number;
    readonly source_height: number;
}
export interface DecodedImage extends AssetBody {
    readonly width: number;
    readonly height: number;
    readonly pixel_format: "rgba8";
}
export interface Entry { key: string; revision: number; chars: number; snippet: string }
export interface EntryChunk {
    key: string;
    revision: number;
    content: string;
    total_chars: number;
    start: number;
    next_offset: number | null;
}
export interface Written { key: string; revision: number; total_chars: number }
export interface DbKey { scope?: Scope; key: string }
export interface StructuredState { key: string; scope: Scope; value: JsonObject; revision: number }

export interface MiniRPApi {
    readonly environment: Environment;
    readonly assets: {
        url(id: string): AssetUrl;
        /** Skill tools can also open authorised Asset URLs supplied in their arguments for this invocation. */
        open(url: AssetUrl, options?: JsonObject): Promise<AssetBody>;
        read(url: AssetUrl, options?: JsonObject): Promise<AssetBytes>;
        describe(url: AssetUrl): Promise<AssetMetadata>;
    };
    readonly text: {
        encode(text: string, options?: { encoding?: TextEncoding }): Promise<Uint8Array>;
        decode(bytes: Uint8Array, options?: { encoding?: TextEncoding }): Promise<string>;
        read(url: AssetUrl, options?: { encoding?: TextEncoding; offset?: number; length?: number }): Promise<TextPage>;
    };
    readonly image: {
        metadata(url: AssetUrl): Promise<JsonObject>;
        preview(url: AssetUrl, options?: { viewport?: ImageViewport }): Promise<RenderedImage>;
        decode(url: AssetUrl): Promise<DecodedImage>;
        encode(url: AssetUrl, options?: { format?: "png" | "jpeg" | "webp"; quality?: number }): Promise<AssetBody>;
    };
    readonly media: {
        videoMetadata(url: AssetUrl): Promise<JsonObject>;
        decodeFrame(url: AssetUrl, options?: { start_ms?: number; viewport?: ImageViewport }): Promise<RenderedImage>;
        decodeAudio(url: AssetUrl, options?: MediaInterval): Promise<AssetBody>;
        encodeAudio(url: AssetUrl, options?: MediaInterval): Promise<AssetBody>;
        encodeVideo(url: AssetUrl, options?: MediaInterval): Promise<AssetBody>;
    };
    readonly pdf: {
        metadata(url: AssetUrl): Promise<JsonObject>;
        renderPage(url: AssetUrl, options?: { page?: number; viewport?: ImageViewport }): Promise<RenderedImage>;
        createPage(url: AssetUrl): Promise<AssetBody>;
    };
    readonly actions: {
        invoke(args: { url: HookUrl; assets?: Record<string, AssetUrl> }): Promise<Accepted>;
        publish(args: { url: EventUrl; assets?: Record<string, AssetUrl> }): Promise<Accepted & { subscribers: number }>;
        publishOutput(args: { text: string; name?: string; mime?: string }): Promise<
            { output: string } | { asset_url: AssetUrl; size: number; instruction: string }
        >;
    };
    readonly attachments: {
        open(args: { id: string }): Promise<AssetBody>;
    };
    readonly picker: {
        media(args?: { type?: "image" | "video" | "both" }): Promise<{ files: SelectedFile[] }>;
        file(args?: { mimeTypes?: string[] }): Promise<{ files: SelectedFile[] }>;
    };
    readonly http: {
        request(args: {
            url: string;
            method?: "GET" | "HEAD" | "POST" | "PUT" | "PATCH" | "DELETE";
            headers?: Record<string, string>;
            body?: string;
            timeout_ms?: number;
        }): Promise<{ status: number; headers: Record<string, string>; body: string }>;
    };
    readonly db: {
        get(args: DbKey & { start?: number; length?: number }): Promise<EntryChunk | { missing: true }>;
        put(args: DbKey & { content: string; expected_revision?: number }): Promise<Written>;
        patch(args: DbKey & { old_string: string; new_string: string; replace_all?: boolean; expected_revision?: number }): Promise<Written>;
        delete(args: DbKey & { expected_revision: number }): Promise<{ deleted: true; key: string }>;
        list(args?: { scope?: Scope; prefix?: string; after?: string; limit?: number; notes_only?: boolean }): Promise<{ entries: Entry[]; next_after: string | null }>;
        search(args: {
            scope?: Scope;
            query: string;
            match?: "all" | "keys" | "content";
            prefix?: string;
            offset?: number;
            limit?: number;
            newest_first?: boolean;
            notes_only?: boolean;
        }): Promise<{
            matches: Array<Omit<Entry, "chars">>;
            next_offset: number | null;
            algorithm: "fts5_trigram" | "literal_scan";
        }>;
    };
    readonly settings: { get(args: { key: string }): Promise<string | boolean | null> };
    readonly state: {
        get(args: { key: string }): Promise<StructuredState>;
        set(args: { key: string; value: JsonObject; expected_revision: number }): Promise<StructuredState>;
    };
    readonly harness: {
        getSessionInfo(): Promise<{
            model: string;
            provider: string;
            contextWindow: number;
            usedTokens: number;
            remainingTokens: number;
        } | null>;
        listPresets(args?: Page): Promise<Array<{ id: UUID; name: string }>>;
        listChats(args?: Page & RoomTarget): Promise<Array<{ id: UUID; name: string; members?: string[] }>>;
        getPreset(args?: { preset?: UUID }): Promise<JsonObject>;
        readChat(args?: Page & ReadTarget): Promise<{ id: UUID; room: UUID; preset: UUID; name: string; messages: Message[] }>;
        listRooms(args?: Page & { preset?: UUID }): Promise<Array<{ id: UUID; name: string }>>;
        readRoom(args?: Page & RoomTarget): Promise<{ id: UUID; preset: UUID; name: string; chats: Array<{ id: UUID; name: string; members: string[] }> }>;
        readSession(args?: Page & ReadTarget & { agent?: SessionAgent; id?: string; participant?: string }): Promise<Message[]>;
        createChat(args?: { preset?: UUID }): Promise<Queued>;
        createRoom(args?: { preset?: UUID }): Promise<Queued>;
        createPreset(args?: { name?: string }): Promise<Queued>;
        send(args: ChatTarget & {
            text?: string;
            stage?: boolean;
            role?: "user" | "system";
            attachments?: Array<SelectedFile & { type?: string }>;
        }): Promise<Queued>;
        steer(args: ChatTarget & { text: string; system?: boolean }): Promise<Accepted>;
        insertSystemMessage(args: ChatTarget & { text: string; participant?: string }): Promise<Accepted | Queued>;
        renameChat(args: ReadTarget & { name: string }): Promise<Queued>;
        renameRoom(args: RoomTarget & { name: string }): Promise<Queued>;
        generateTitle(args?: ReadTarget): Promise<Queued>;
        setSearchMode(args: ReadTarget & { enabled: boolean }): Promise<Queued>;
        setPrompt(args: ReadTarget & { agent?: Exclude<SessionAgent, "temporary"> | "title"; text: string }): Promise<Queued>;
        branchChat(args: ReadTarget & { block: string }): Promise<Queued>;
        createTemporarySession(args?: Record<string, never>): Promise<{ id: string }>;
        runTemporary(args: { id: string; text: string; system?: string }): Promise<{ text: string }>;
        closeTemporary(args: { id: string }): Promise<{ closed: true }>;
        generate(args: { text: string; system?: string }): Promise<{ text: string }>;
    };
}

export interface PluginEvent {
    readonly name: string;
    readonly [key: string]: unknown;
}
export interface StateChangedEvent extends PluginEvent, StructuredState {
    readonly name: "state.changed";
    readonly previous: JsonObject;
}
export interface ActionEvent extends PluginEvent {
    readonly name: "actions.invoke" | "actions.publish";
    readonly url: HookUrl | EventUrl;
    readonly sender: string;
    readonly assets: Readonly<Record<string, AssetUrl>>;
}
export interface AssetRequest extends PluginEvent {
    readonly name: "asset.requested";
    readonly url: AssetUrl;
    readonly request: JsonObject;
    readonly sender: string | null;
    readonly attachment?: SelectedFile;
}
export interface AttachmentEvent extends PluginEvent {
    readonly name: "attachment.process";
    readonly attachment: SelectedFile & { plugin: string; type: string };
}
export interface SkillToolEvent extends PluginEvent {
    readonly name: "skill.tool";
    readonly skill: string;
    readonly tool: string;
    readonly arguments: JsonObject;
}
export type Handler<E extends PluginEvent = PluginEvent, R = Json | void> = (event: E, api: MiniRPApi) => R | Promise<R>;

/** @deprecated Use MiniRPApi. */
export type MinichatApi = MiniRPApi;
export type AssetHandler = Handler<AssetRequest, Uint8Array | ReadableStream<Uint8Array>>;
export type ActionHandler = Handler<ActionEvent>;
export type AttachmentHandler = Handler<AttachmentEvent, { text: string }>;

export function defineHandlers<const T extends Record<string, (event: never, api: MiniRPApi) => unknown>>(handlers: T): T;
export function hookUrl(packageName: string, id: string): HookUrl;
export function eventUrl(packageName: string, id: string): EventUrl;
export function assetUrl(packageName: string, id: string): AssetUrl;
export function workspaceId(value: unknown): UUID;

declare global {
    var handlers: Record<string, (event: never, api: MiniRPApi) => unknown>;
}
