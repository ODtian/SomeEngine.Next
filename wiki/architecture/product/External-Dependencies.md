# External Dependencies

External source dependencies are explicit repository dependencies, preferably tracked through git submodules when source is vendored by reference. Build artifacts and restored packages are not source ownership.

Third-party APIs may define concepts SomeEngine directly uses. When a dependency such as Slang# is the chosen semantic source, missing binding capability is fixed in the maintained fork instead of creating a competing SomeEngine abstraction.

参见 [[architecture/shader/Slang-Authority]]。
