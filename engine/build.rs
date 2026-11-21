fn main() {
    println!("cargo:rerun-if-changed=native/log_encoding.c");
    println!("cargo:rerun-if-changed=native/path.cpp");
    cc::Build::new()
        .file("native/log_encoding.c")
        .compile("errorhunt_input");
    cc::Build::new()
        .cpp(true)
        .std("c++17")
        .file("native/path.cpp")
        .compile("errorhunt_paths");
}
