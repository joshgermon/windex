//! The Model JSON the UI renders (docs/contract.md "Model"): hosts → sessions → windows (tabs)
//! → panes, with each window's layout normalized to a binary split tree.
use super::activity::{self, Activity};
use super::endpoint;
use super::state::{Block, Host, Session, State, tiled_blocks};
use super::transfer;
use rexrs::api::Direction;
use rexrs::api::session::{ViewLayoutNode, Window};
use serde_json::{Value, json};

/// The default tab label: a tab that still has it shows its program's title.
const DEFAULT_TAB_LABEL: &str = "Shell";

pub fn build(st: &State) -> Value {
    json!({
        "type": "model",
        "revision": st.revision,
        "current": st.current.as_ref().map(|(host, session)| json!({"host": host, "session": session})),
        "hosts": st.hosts.iter().map(host).collect::<Vec<_>>(),
        "transfers": st.transfers.iter().map(|t| t.json()).collect::<Vec<_>>(),
    })
}

fn host(h: &Host) -> Value {
    json!({
        "id": h.id,
        "name": h.name,
        "endpoint": h.endpoint,
        "kind": endpoint::kind_of(&h.endpoint),
        "state": h.status.as_str(),
        "error": h.error,
        "server": h.server.as_ref().map(|s| json!({"version": s.version, "api": s.api, "instance": s.instance, "label": s.label})),
        // What the server's API revision allows (docs/contract.md "Transfers").
        "can_upload": h.server.as_ref().is_some_and(|s| s.api >= transfer::UPLOAD_API),
        "can_download": h.server.as_ref().is_some_and(|s| s.api >= transfer::DOWNLOAD_API),
        "sessions": h.sessions.iter().map(|s| session(h, s)).collect::<Vec<_>>(),
    })
}

fn session(h: &Host, s: &Session) -> Value {
    let windows: Vec<Value> = s
        .view
        .iter()
        .flat_map(|v| v.windows.iter().enumerate())
        .map(|(index, w)| window(h, w, index))
        .collect();
    // The session's word: the most pressing among its tabs.
    let rank = |tone: &str| match tone {
        "danger" => 3,
        "warning" => 2,
        _ => 1,
    };
    let word = windows
        .iter()
        .filter_map(|w| Some((w["word"].as_str()?, w["tone"].as_str()?)))
        .max_by_key(|(_, tone)| rank(tone));
    json!({
        "id": s.id,
        "label": s.label,
        "attached": s.attached,
        "word": word.map(|w| w.0),
        "tone": word.map(|w| w.1),
        "active_window": s.view.as_ref().and_then(|v| v.active_window_id.clone()),
        "windows": windows,
    })
}

fn window(h: &Host, w: &Window, index: usize) -> Value {
    let pane_of = |block: &str| h.handles.get(block).copied();
    let blocks: Vec<(&str, &Block, Option<&str>, Option<&str>)> = tiled_blocks(w)
        .filter_map(|lb| {
            let id = lb.block_id.as_deref()?;
            let block = h.blocks.get(id)?;
            Some((id, block, lb.label.as_deref(), lb.flavor.as_deref()))
        })
        .collect();
    let activities = blocks.iter().map(|(_, b, _, _)| &b.activity);
    let (word, tone) = activity::tab_word(activities.clone()).map_or((None, None), |(w, t)| (Some(w), Some(t)));
    let focused_block = w
        .focused_block_id
        .as_deref()
        .filter(|id| blocks.iter().any(|(b, ..)| b == id))
        .or(blocks.first().map(|(id, ..)| *id));
    let focused_title = focused_block
        .and_then(|id| blocks.iter().find(|(b, ..)| *b == id))
        .map(|(_, block, label, flavor)| pane_title(block, *label, *flavor));
    let label = w.label.clone().unwrap_or_default();
    let title = if label.is_empty() || label == DEFAULT_TAB_LABEL {
        focused_title.unwrap_or_else(|| if label.is_empty() { "Terminal".into() } else { label.clone() })
    } else {
        label.clone()
    };
    let layout = w
        .layers
        .iter()
        .find(|l| l.kind == "tiled")
        .or(w.layers.first())
        .and_then(|l| l.layout.as_ref())
        .and_then(|node| layout(node, &pane_of));
    json!({
        "id": w.window_id,
        "label": label,
        "title": title,
        "index": index,
        "focused_pane": focused_block.and_then(pane_of).unwrap_or(0),
        "zoomed_pane": w.zoomed_block_id.as_deref().and_then(pane_of),
        "word": word,
        "tone": tone,
        "progress": activity::tab_progress(activities).map(activity::Progress::json),
        "layout": layout,
        "panes": blocks.iter().map(|(id, block, label, flavor)| pane(h, id, block, *label, *flavor)).collect::<Vec<_>>(),
    })
}

/// A pane's name: its user label, or while that is still the default (the flavor's last
/// word, `shell`), its terminal's title.
fn pane_title(block: &Block, label: Option<&str>, flavor: Option<&str>) -> String {
    let label = label.filter(|l| !l.is_empty());
    let default = block.terminal && label.is_none_or(|l| flavor.and_then(|f| f.rsplit('.').next()) == Some(l));
    let title = default.then(|| block.activity.shown_title()).flatten();
    title.or(label.map(str::to_owned)).unwrap_or_else(|| "Terminal".into())
}

fn pane(h: &Host, id: &str, block: &Block, label: Option<&str>, flavor: Option<&str>) -> Value {
    let a: &Activity = &block.activity;
    let link = block.link.as_ref();
    let owned = match link {
        Some(link) => link.owned(),
        None => block.owner.is_some() && block.owner == h.client_id,
    };
    let size = link.and_then(|l| l.server_size()).or(block.server_size);
    json!({
        "pane": block.pane,
        "block": id,
        "title": pane_title(block, label, flavor),
        "program": a.program,
        "cwd": a.working_directory(),
        "icon": a.icon(),
        "status": a.status().map(|(word, tone, message)| json!({"word": word, "tone": tone, "message": message})),
        "progress": a.shown_progress().map(activity::Progress::json),
        // Input pauses while a streamed pane's transport is down.
        "paused": match link {
            Some(link) if block.stream.is_some() => link.paused(),
            _ => !h.connected(),
        },
        "owned": owned,
        "server_cols": size.map_or(0, |s| s.0),
        "server_rows": size.map_or(0, |s| s.1),
        "exited": a.exited || link.is_some_and(|l| l.exited()),
    })
}

/// The server's layout tree (§3.3) → the contract's binary split tree. A node without a pane
/// handle (or an empty side) collapses into its other side.
pub fn layout(node: &ViewLayoutNode, pane_of: &impl Fn(&str) -> Option<u64>) -> Option<Value> {
    if let Some(block) = &node.block_id {
        return pane_of(block).map(|pane| json!({"kind": "pane", "pane": pane}));
    }
    let split = node.split.as_ref()?;
    let before = split.before.as_deref().and_then(|n| layout(n, pane_of));
    let after = split.after.as_deref().and_then(|n| layout(n, pane_of));
    match (before, after) {
        (Some(before), Some(after)) => {
            let ratio = if split.ratio.is_finite() && split.ratio > 0.0 && split.ratio < 1.0 { split.ratio } else { 0.5 };
            let axis = match split.direction {
                Direction::Vertical => "column",
                _ => "row",
            };
            Some(json!({
                "kind": "split",
                "id": split.split_id,
                "axis": axis,
                "ratio": ratio,
                "children": [{"weight": ratio, "node": before}, {"weight": 1.0 - ratio, "node": after}],
            }))
        }
        (Some(only), None) | (None, Some(only)) => Some(only),
        (None, None) => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn node(value: Value) -> ViewLayoutNode {
        serde_json::from_value(value).unwrap()
    }

    fn panes(block: &str) -> Option<u64> {
        match block {
            "block:a" => Some(1),
            "block:b" => Some(2),
            "block:c" => Some(3),
            _ => None,
        }
    }

    #[test]
    fn single_pane() {
        let n = node(json!({"rect":[0,0,1,1],"block_id":"block:a"}));
        assert_eq!(layout(&n, &panes), Some(json!({"kind":"pane","pane":1})));
    }

    #[test]
    fn nested_splits_from_the_live_capture() {
        // Appendix A.5, revision 20: (a / b) | c at 0.6.
        let n = node(json!({"rect":[0,0,1,1],"split":{"split_id":"split:outer","direction":"horizontal","ratio":0.6,
            "before":{"rect":[0,0,0.6,1],"split":{"split_id":"split:inner","direction":"vertical","ratio":0.5,
                "before":{"rect":[0,0,0.6,0.5],"block_id":"block:a"},
                "after":{"rect":[0,0.5,0.6,0.5],"block_id":"block:b"}}},
            "after":{"rect":[0.6,0,0.4,1],"block_id":"block:c"}}}));
        let out = layout(&n, &panes).unwrap();
        assert_eq!(out["kind"], "split");
        assert_eq!(out["id"], "split:outer");
        assert_eq!(out["axis"], "row");
        assert_eq!(out["ratio"], 0.6);
        assert_eq!(out["children"][0]["weight"], 0.6);
        assert!((out["children"][1]["weight"].as_f64().unwrap() - 0.4).abs() < 1e-9);
        let inner = &out["children"][0]["node"];
        assert_eq!((inner["axis"].as_str(), inner["id"].as_str()), (Some("column"), Some("split:inner")));
        assert_eq!(inner["children"][1]["node"], json!({"kind":"pane","pane":2}));
        assert_eq!(out["children"][1]["node"], json!({"kind":"pane","pane":3}));
    }

    #[test]
    fn unknown_sides_collapse_and_bad_ratios_even_out() {
        let n = node(json!({"split":{"split_id":"s","direction":"diagonal","ratio":1.5,
            "before":{"block_id":"block:a"},"after":{"block_id":"block:zzz"}}}));
        assert_eq!(layout(&n, &panes), Some(json!({"kind":"pane","pane":1})));
        let n = node(json!({"split":{"split_id":"s","direction":"diagonal","ratio":0,
            "before":{"block_id":"block:a"},"after":{"block_id":"block:b"}}}));
        let out = layout(&n, &panes).unwrap();
        assert_eq!((out["axis"].as_str(), out["ratio"].as_f64()), (Some("row"), Some(0.5)));
        assert_eq!(layout(&node(json!({"rect":[0,0,1,1]})), &panes), None);
    }
}
