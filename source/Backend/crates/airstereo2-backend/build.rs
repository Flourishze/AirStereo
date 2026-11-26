use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

fn find_rc_exe() -> Option<PathBuf> {
    if let Ok(path) = env::var("RC_EXE") {
        let p = PathBuf::from(path);
        if p.is_file() {
            return Some(p);
        }
    }
    let kits_root = Path::new(r"C:\Program Files (x86)\Windows Kits\10\bin");
    if let Ok(entries) = fs::read_dir(kits_root) {
        let mut candidates: Vec<PathBuf> = entries
            .filter_map(|e| e.ok())
            .map(|e| e.path().join("x64").join("rc.exe"))
            .filter(|p| p.is_file())
            .collect();
        candidates.sort();
        if let Some(latest) = candidates.pop() {
            return Some(latest);
        }
    }
    Some(PathBuf::from("rc.exe"))
}

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }

    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let icon_src = manifest_dir
        .join("..")
        .join("..")
        .join("..")
        .join("Shell")
        .join("AirStereo2.ico");
    println!("cargo:rerun-if-changed={}", icon_src.display());

    if !icon_src.is_file() {
        panic!("AirStereo2.ico not found at {}", icon_src.display());
    }

    let icon_dst = out_dir.join("AirStereo2.ico");
    fs::copy(&icon_src, &icon_dst).expect("copy AirStereo2.ico to OUT_DIR");

    let pkg_ver = env::var("CARGO_PKG_VERSION").unwrap_or_else(|_| "1.1.1".to_string());
    let nums: Vec<u16> = pkg_ver
        .split('.')
        .take(3)
        .map(|s| s.parse::<u16>().unwrap_or(0))
        .collect();
    let (maj, min, pat) = (
        *nums.first().unwrap_or(&1),
        *nums.get(1).unwrap_or(&1),
        *nums.get(2).unwrap_or(&1),
    );
    let file_ver_str = format!("{maj}.{min}.{pat}.0");

    let rc_content = format!(
        r#"1 ICON "AirStereo2.ico"

1 VERSIONINFO
FILEVERSION {maj},{min},{pat},0
PRODUCTVERSION {maj},{min},{pat},0
FILEOS 0x40004
FILETYPE 0x1
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904B0"
    BEGIN
      VALUE "CompanyName", "Flourishze\0"
      VALUE "FileDescription", "AirStereo2 Audio Engine\0"
      VALUE "FileVersion", "{file_ver_str}\0"
      VALUE "InternalName", "AirStereo2.Engine\0"
      VALUE "LegalCopyright", "Copyright (c) 2026 Flourishze\0"
      VALUE "OriginalFilename", "AirStereo2.Engine.exe\0"
      VALUE "ProductName", "AirStereo2\0"
      VALUE "ProductVersion", "{pkg_ver}\0"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x0409, 1200
  END
END
"#
    );

    let rc_path = out_dir.join("airstereo2_engine.rc");
    let res_path = out_dir.join("airstereo2_engine.res");
    fs::write(&rc_path, rc_content).expect("write airstereo2_engine.rc");

    let rc_exe = find_rc_exe().expect("Windows SDK rc.exe not found");
    let status = Command::new(&rc_exe)
        .current_dir(&out_dir)
        .args(["/nologo", "/fo"])
        .arg(&res_path)
        .arg(&rc_path)
        .status()
        .unwrap_or_else(|e| panic!("failed to run {}: {e}", rc_exe.display()));
    assert!(
        status.success(),
        "rc.exe failed with status {status}"
    );

    println!(
        "cargo:rustc-link-arg-bin=airstereo2-engine={}",
        res_path.display()
    );
}
