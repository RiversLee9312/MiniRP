# Stream assets plugin

The example in `plugin/` can be installed for any preset. When adapting it for one preset, add the preset UUID to the manifest before signing. The fragment below assigns the example to preset `d4343dda-0747-44c9-9304-329d11e34c22`.

```json
{
  "presetId": "d4343dda-0747-44c9-9304-329d11e34c22"
}
```

See [packaging and installation](../../docs/plugins/packaging.md).

Activate `stream-example-resources` and call `plugin_stream-example_sample_url` to receive the generated resource's URL. Give that URL or a user attachment URL to `plugin_stream-example_measure` to count the bytes. The tool receives access to the supplied URL for that call. The `assets` skill can inspect its metadata.

`plugin_stream-example_about_url` returns the URL of a dynamic Asset. Its handler reads Markdown via `api.text.read` and encodes it with `api.text.encode`. The tool and caller exchange the URL. The host runs the codec when the resource is opened.

Running the plugin changes the generated sample and calls `api.assets.notifyChanged(url)`. The manifest declares a change event for the sample and subscribes its own Hook. The Hook opens the Asset URL from `event.assets.asset` and records the sample size for the active preset.

For a preset-owned background, add `"backgroundVariants": {"square":"scene-square","landscape":"scene-wide","portrait":"scene-tall"}` to the primary media Asset. Declare those three IDs as media Assets in the same manifest. The preset uses the primary Asset URL. [Background manifest example](../../docs/plugins/actions-assets.md#预设聊天背景)
