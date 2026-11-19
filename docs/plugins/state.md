# 数据库与结构化状态

## DB

持久化使用 SQLite。插件只能打开自己的 `session`、`room`、`chat` 或 `preset` 分区，默认 `session`。所有方法需 `db` 权限。键是 1–500 字符的字符串。写入和替换文本上限 256 Ki UTF-16 字符。通过 `revision` 防止覆盖其他调用已经提交的修改。

| API | 参数与结果 |
| --- | --- |
| `db.get` | `{scope?,key,start?,length?}` → 未找到时为 `{missing:true}`，否则为 `{key,revision,content,total_chars,start,next_offset}`。`length` 默认 12000，最大 64000；`next_offset:null` 表示读完。 |
| `db.put` | `{scope?,key,content,expected_revision?}` → `{key,revision,total_chars}`。已有键需要匹配当前修订号。创建时可传 `0`。 |
| `db.patch` | `{scope?,key,old_string,new_string,replace_all?,expected_revision?}` → `{key,revision,total_chars}`。默认要求 `old_string` 唯一，找不到或竞争时拒绝。 |
| `db.delete` | `{scope?,key,expected_revision}` → `{deleted:true,key}`。 |
| `db.list` | `{scope?,prefix?,after?,limit?,notes_only?}` → `{entries,next_after}`。`entries` 包含 `key,revision,chars,snippet`；将上一页 `next_after` 作为下一页 `after`。 |
| `db.search` | `{scope?,query,match?,prefix?,offset?,limit?,newest_first?,notes_only?}` → `{matches,next_offset,algorithm}`。`match` 选 `all`、`keys` 或 `content`，采用 SQLite FTS5 trigram 或字面搜索。 |

`db.list`、`db.search` 的 `limit` 默认 30，范围 1–200。`notes_only` 可排除以下划线开头的记录。
## 结构化状态与卡片

`api.environment` 可提供 `session`、`chat`、`room`、`preset`、`agent` 和群像中的 `participant`。如果本次事件没有相应对象，其字段可缺失或为 `null`。DB API 与结构化状态都支持 `session`、`room`、`chat` 和 `preset`。普通对话中 `room` 与 `chat` 指同一个存储作用域；群像的 Room scope 覆盖其内所有 Chat，Chat scope 由宿主按 Room 与 Chat 两级隔离。

| API | 参数与结果 |
| --- | --- |
| `state.get` | `{key}` → `{key,scope,value,revision}`。只能读本插件声明且当前有对应上下文的条目。 |
| `state.set` | `{key,value,expected_revision}` → 同上。修订需匹配，`value` 是最多 32,768 字符的 JSON 对象。 |
| `stateEntries[].onChange` | 保存新值后，向本包 Hook 发送 `{name:"state.changed",key,scope,value,previous,revision}`。写入相同值沿用原修订。 |
| `skills[].stateEntries` | 允许指定角色读取本插件的声明条目。生成的读取工具返回 `db_key` 供 `db_read`、`db_watch` 使用。由插件业务工具处理修改。 |
| `skills[].cards` | 允许指定角色引用本插件声明的卡片。卡片绑定的状态通过当前作用域读取。 |

声明了 `onChange` 的条目会在写入 SQLite 前为通知预留队列位置。队列满时，`state.set` 返回错误，值和修订号保留。调用方可稍后用当前修订号重试。

完整字段、示例程序、订阅行为及事件防重见 [插件联动指南](automation.md)。
