//! Windex core: Rex protocol client, SSH forwarding and libghostty-vt terminals behind a C ABI
//! (see include/rexcore.h and docs/contract.md).
pub mod core;
pub mod events;
pub mod ffi;
pub mod frame;
pub mod pane;
pub mod remote;
pub mod search;
pub mod selection;

pub use crate::core::{Core, Options};
