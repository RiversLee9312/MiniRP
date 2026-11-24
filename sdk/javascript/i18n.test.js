import assert from "node:assert/strict";
import { test } from "node:test";
import { defineI18n, getLocale, localePointer } from "./index.js";

test("reads the locale for each call and constructs JSON Pointers with escaping", () => {
    const api = { environment: { locale: "fr-CA" } };
    assert.equal(getLocale(api), "fr-CA");
    api.environment.locale = "fr_CA";
    assert.equal(getLocale(api, "zh-Hant"), "zh-Hant");
    assert.equal(getLocale(api, ""), "en");
    assert.equal(localePointer("cards", 0, "labels", "ready"), "/cards/0/labels/ready");
    assert.equal(localePointer("skills", 0, "a~/b"), "/skills/0/a~0~1b");
    assert.throws(() => localePointer(), TypeError);
});

test("uses MiniRP locale aliases after parents and before the declared default", () => {
    const t = defineI18n({ defaultLocale: "en", locales: {
        en: { status: "Status" },
        zh: { parent: "中文" },
        "zh-Hans": { status: "状态" },
        "zh-Hant": { status: "狀態", parent: "繁體中文" },
        "pt-BR": { status: "Estado" },
    } });
    assert.equal(t("zh-TW", "status"), "狀態");
    assert.equal(t("zh-HK", "parent"), "中文");
    assert.equal(t("zh-Hans-TW", "status"), "状态");
    assert.equal(t("zh-CN", "status"), "状态");
    assert.equal(t("pt-PT", "status"), "Estado");
});

test("resolves each key by exact locale, parents, default and original message", () => {
    const t = defineI18n({
        defaultLocale: "en-US",
        source: { original: "Original", inherited: " " },
        locales: {
            "zh-Hans-CN": { heading: "地区标题", summary: "" },
            "zh-Hans": { heading: "简体标题", summary: "简体摘要" },
            zh: { region: "地区" },
            "en-US": { notice: "Default notice" },
            en: { region: "Region", retries: "Try again" },
        },
    });

    assert.equal(t("ZH-hans-cn", "heading"), "地区标题");
    assert.equal(t("zh-Hans-CN", "summary"), "简体摘要");
    assert.equal(t("zh-Hans-SG", "region"), "地区");
    assert.equal(t("ja", "notice"), "Default notice");
    assert.equal(t(null, "retries"), "Try again");
    assert.equal(t(undefined, "original"), "Original");
    assert.equal(t("zh_CN", "region"), "Region");
    assert.equal(t("zh-Hans-CN\n", "heading"), "heading");
    assert.equal(t("en", "inherited"), "inherited");
    assert.equal(t("en", "toString"), "toString");
    assert.equal(t("en", "   "), "");
});

test("truncates BCP 47 extensions and private-use tags without orphaned singletons", () => {
    const t = defineI18n({
        locales: {
            "en-US-u-ca": { month: "Calendar month" },
            "en-US": { heading: "US heading" },
            en: { message: "English" },
            "x-qa": { heading: "Fixture" },
            "i-klingon": { heading: "Grandfathered tag" },
        },
    });

    assert.equal(t("en-US-u-ca-gregory", "month"), "Calendar month");
    assert.equal(t("en-US-u-nu-latn", "heading"), "US heading");
    assert.equal(t("en-GB-x-testing", "message"), "English");
    assert.equal(t("x-qa-testing", "heading"), "Fixture");
    assert.equal(t("i-KLINGON", "heading"), "Grandfathered tag");
});

test("formats reordered and repeated numeric placeholders without expanding inserted text", () => {
    const t = defineI18n({
        locales: { en: { sample: "{2} · {1} · {2} · {3} · {4} · {10} · {0}" } },
    });

    assert.equal(t("en", "sample", 0, "$& {3}", false, null), "$& {3} · 0 · $& {3} · false · {4} · {10} · {0}");
});

test("rejects invalid declared locales and case-insensitive duplicates", () => {
    assert.throws(() => defineI18n({ defaultLocale: "en_US", locales: {} }), TypeError);
    assert.throws(() => defineI18n({ defaultLocale: "en-u-ca-u-nu", locales: {} }), TypeError);
    assert.throws(() => defineI18n({ locales: { "zh_Hans": {} } }), TypeError);
    assert.throws(() => defineI18n({ locales: { "en-1901-1901": {} } }), TypeError);
    assert.throws(() => defineI18n({ locales: { en: {}, EN: {} } }), TypeError);
});
