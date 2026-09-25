//! Generates both bindings from this crate's source, so neither can drift from the ABI:
//! `ripcord.h` for C and Swift (cbindgen), and `NativeMethods.g.cs` for .NET (csbindgen). Both land in
//! `<target>/include/`, and neither is committed.

use std::path::PathBuf;

fn main() {
    let crate_dir = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let target_dir = std::env::var_os("CARGO_TARGET_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| crate_dir.join("../target"));
    let include = target_dir.join("include");
    std::fs::create_dir_all(&include).expect("create the include directory");

    println!("cargo:rerun-if-changed=src");
    println!("cargo:rerun-if-changed=cbindgen.toml");

    let config = cbindgen::Config::from_file(crate_dir.join("cbindgen.toml")).expect("read cbindgen.toml");
    cbindgen::Builder::new()
        .with_crate(&crate_dir)
        .with_config(config)
        .generate()
        .expect("generate ripcord.h")
        .write_to_file(include.join("ripcord.h"));

    csbindgen::Builder::default()
        .input_extern_file(crate_dir.join("src/lib.rs"))
        .csharp_dll_name("ripcord")
        .csharp_namespace("Ripcord.Engine.Native")
        .csharp_class_name("NativeMethods")
        .csharp_class_accessibility("internal")
        .csharp_use_function_pointer(true)
        .always_included_types(["RipcordStructId"])
        .generate_csharp_file(include.join("NativeMethods.g.cs"))
        .expect("generate NativeMethods.g.cs");
}
