export type Json = null | boolean | number | string | Json[] | { [name: string]: Json };
export type JsonObject = { [name: string]: Json };
export type HookUrl = `minichat://actions/${string}/hooks/${string}`;
export type EventUrl = `minichat://actions/${string}/events/${string}`;
export type AssetUrl = `minichat://assets/${string}/${string}`;
export type ConversationAgent = "chat" | "env" | "preset";
export type SessionAgent = ConversationAgent | "search" | "fetch" | "temporary";
export type Scope = "session" | "chat" | "preset";
export type ReadTarget = { preset?: string; chat?: string };
export type ChatTarget = { chat?: string; agent?: ConversationAgent };
export type Page = { offset?: number; limit?: number };

export interface Environment {
    readonly session?: string | null;
    readonly chat?: string | null;
    readonly preset?: string | null;
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
}

export interface Queued { queued: true; request: string }
export interface Accepted { accepted: true }
export interface AssetBody {
    readonly mimeType: string;
    readonly length: number | null;
    readonly body: ReadableStream<Uint8Array>;
}
export interface AssetBytes { readonly mimeType: string; readonly bytes: Uint8Array }
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

export interface MinichatApi {
    readonly environment: Environment;
    readonly assets: {
        url(id: string): AssetUrl;
        open(url: AssetUrl, options?: JsonObject): Promise<AssetBody>;
        read(url: AssetUrl, options?: JsonObject): Promise<AssetBytes>;
    };
    readonly actions: {
        invoke(args: { url: HookUrl; assets?: Record<string, AssetUrl> }): Promise<Accepted>;
        publish(args: { url: EventUrl; assets?: Record<string, AssetUrl> }): Promise<Accepted & { subscribers: number }>;
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
    readonly harness: {
        listPresets(args?: Page): Promise<Array<{ id: string; name: string }>>;
        listChats(args?: Page & { preset?: string }): Promise<Array<{ id: string; name: string }>>;
        getPreset(args?: { preset?: string }): Promise<JsonObject>;
        readChat(args?: Page & ReadTarget): Promise<{ id: string; preset: string; name: string; messages: Message[] }>;
        readSession(args?: Page & ReadTarget & { agent?: SessionAgent; id?: string }): Promise<Message[]>;
        createChat(args?: { preset?: string }): Promise<Queued>;
        createPreset(args?: { name?: string }): Promise<Queued>;
        send(args: ChatTarget & {
            text?: string;
            stage?: boolean;
            role?: "user" | "system";
            attachments?: Array<SelectedFile & { type?: string }>;
        }): Promise<Queued>;
        steer(args: ChatTarget & { text: string; system?: boolean }): Promise<Accepted>;
        insertSystemMessage(args: ChatTarget & { text: string }): Promise<Accepted | Queued>;
        renameChat(args: { chat?: string; name: string }): Promise<Queued>;
        generateTitle(args?: { chat?: string }): Promise<Queued>;
        setSearchMode(args: { chat?: string; enabled: boolean }): Promise<Queued>;
        setPrompt(args: { chat?: string; agent?: Exclude<SessionAgent, "temporary"> | "title"; text: string }): Promise<Queued>;
        branchChat(args: { chat?: string; block: string }): Promise<Queued>;
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
export type Handler<E extends PluginEvent = PluginEvent, R = Json | void> = (event: E, api: MinichatApi) => R | Promise<R>;
export type AssetHandler = Handler<AssetRequest, Uint8Array | ReadableStream<Uint8Array>>;
export type ActionHandler = Handler<ActionEvent>;
export type AttachmentHandler = Handler<AttachmentEvent, { text: string }>;

export function defineHandlers<const T extends Record<string, (event: never, api: MinichatApi) => unknown>>(handlers: T): T;
export function hookUrl(packageName: string, id: string): HookUrl;
export function eventUrl(packageName: string, id: string): EventUrl;
export function assetUrl(packageName: string, id: string): AssetUrl;

declare global {
    var handlers: Record<string, (event: never, api: MinichatApi) => unknown>;
}
