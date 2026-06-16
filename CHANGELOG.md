# Changelog

## [2.2.1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v2.2.0...v2.2.1) (2026-06-16)


### Bug Fixes

* **symbols:** tolerate ErrorTypeSymbol in inheritance index ([#222](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/222)) ([#223](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/223)) ([d3b61d8](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/d3b61d81c614a7495687f236b48acefd309aa2cd))

## [2.2.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v2.1.0...v2.2.0) (2026-06-10)


### Features

* **symbols:** SymbolSignatureComparer for cross-compilation identity in enumeration paths ([#214](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/214)) ([b34f8c6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/b34f8c658d1ee7122c30a2706dacb5fb987e3c3b))


### Bug Fixes

* **bg-tasks:** set Result/ErrorCode/ErrorMessage before flipping Status to terminal ([#212](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/212)) ([272f8ad](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/272f8adb54026dc40c99771adcc78519afd2bc9f))
* **find_references,find_callers:** use SymbolFinder for cross-compilation symbol identity ([#211](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/211)) ([7283122](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/7283122abd4c457354814a18eea43c8b33cf1dde))

## [2.1.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v2.0.0...v2.1.0) (2026-05-28)


### Features

* MSBuild timeout + background task store ([#206](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/206)) ([5403cf8](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/5403cf8358eb50afb969b018f63c3ebb58ba4e61))

## [2.0.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.19.0...v2.0.0) (2026-05-26)


### ⚠ BREAKING CHANGES

* **errors:** structured tool errors + cancellation pass-through ([#200](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/200))

### Features

* **errors:** structured tool errors + cancellation pass-through ([#200](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/200)) ([6cad043](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/6cad043f163f6c2a3a0e6dceab84eae618f69a67))

## [1.19.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.18.0...v1.19.0) (2026-05-22)


### Features

* filter false positives from find_unused_symbols (test/MCP/generated/MEF/interop) ([#191](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/191)) ([c6d204f](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/c6d204fd1417a6e3e1d4bba0ea22a1e6888fbe69))

## [1.18.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.17.1...v1.18.0) (2026-05-22)


### Features

* uniform ToolListResult&lt;T&gt; envelope for all list-returning tools ([#189](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/189)) ([7539ffd](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/7539ffd6391efa7badf1bc67c7c4e7c2fffc044a))

## [1.17.1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.17.0...v1.17.1) (2026-05-17)


### Bug Fixes

* gracefully handle legacy non-SDK csproj projects ([#175](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/175)) ([#178](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/178)) ([8963e78](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/8963e78b8ef7987356558e1a3c8ef20adb23a840))

## [1.17.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.16.0...v1.17.0) (2026-05-11)


### Features

* **security:** trust gate + analyzer allowlist for GHSA-552p-8f74-6x7q ([#168](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/168)) ([eab0164](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/eab01643e3c7ee874804b6a9752ae1811008764a))

## [1.16.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.15.0...v1.16.0) (2026-05-04)


### Features

* add get_operators MCP tool ([fe03ed3](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/fe03ed32d6cd27960020a2e16634107362bd5988))
* add models for get_operators ([ef384b6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/ef384b6459658657711dcb7102f0afa0124421ba))
* implement GetOperatorsLogic with operator-kind mapping ([e44bfbe](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/e44bfbea8b8fbd8b8c2446c8839f7512d4c7c428))
* register get_operators MCP tool ([3294ed0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/3294ed03a8e0ac33e16ce97100c1d0130043620b))


### Bug Fixes

* avoid angle brackets in get_operators description so MDX docs build succeeds ([d43597f](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/d43597f2b5d154520fcc0802abced3e7aee0da64))
* rename CallGraphSamples Money to CallGraphMoney to disambiguate from OperatorSamples Money ([f3da09c](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/f3da09c812016edf4a33bfc1630fca13448dbc93))

## [1.15.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.14.0...v1.15.0) (2026-05-02)


### Features

* add GenerateTestSkeletonLogic single-method case ([e2905be](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/e2905be99fe39371d9710090002a1f1af3339ee4))
* add model for generate_test_skeleton ([e5aece4](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/e5aece4888e13bfda0e899a254859a1c0a61fc6b))
* add TestFrameworkDetector to identify per-project framework ([71742c7](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/71742c74697e5b255a012636c2e0e94b0af613d9))
* cover static-method skeleton generation ([f5042bd](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/f5042bd6f2edefb91831880830b449d0b8fec175))
* emit Assert.Throws stub per distinct exception type ([e942ef3](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/e942ef35c177bb584c65cb415f5953dfec650ffc))
* emit async stub for Task-returning methods ([4a7c318](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/4a7c3183eae5d2e78cd1c4d0825a9470bd953876))
* emit Theory + InlineData for primitive-param methods ([431fdb0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/431fdb02b0840d30a32f5b212af0fcd631c94618))
* generate_test_skeleton ([fe08c01](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/fe08c01d629e3c6fb416e6633aa9bdf8419a9bc3))
* register generate_test_skeleton MCP tool ([d8a40c1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/d8a40c1ff5e9d72443bb385565e54d93e6da271d))
* surface constructor dependencies as TodoNotes ([9875a04](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/9875a04836253980031f1e4ae240d6e08be54dae))


### Bug Fixes

* enforce generated-code skip + parametric throw-stub args ([2527144](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/25271447bdb622a147a2b733406826450d81c478))
* escape generic angle brackets in tool description for MDX ([a3df3a7](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/a3df3a7098edc55a31e162cf654836ea80b0d959))

## [1.14.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.13.0...v1.14.0) (2026-05-01)


### Features

* add get_test_summary MCP tool ([64a79e7](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/64a79e7372a311c76f8d3b6faef9cba93a69a496))
* add GetTestSummaryLogic with tests ([a6eec21](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/a6eec2191c3df3d0a1cff9f1990e389a93afb81e))
* add models for get_test_summary ([0f7c664](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/0f7c664eb7e375fed1ce8ac8564ca3dac566e1ba))
* register get_test_summary MCP tool ([a4ae431](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/a4ae4316e38e029071207731c7d88dccd96e2e09))


### Bug Fixes

* enforce generated-code skip + narrow Microsoft.* exclusion ([345f489](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/345f4899c605961ebdfa2407eb84cf03103938ab))

## [1.13.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.12.0...v1.13.0) (2026-05-01)


### Features

* add find_obsolete_usage MCP tool ([e27e370](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/e27e370a69109c64641ab590d83bfd501de872cb))
* add FindObsoleteUsageLogic grouped by deprecation message ([5d253ae](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/5d253aece34951636c0f61a1312443a425ae5e7f))
* add get_overloads MCP tool ([6c332c1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/6c332c175d4b2625cb7138d5c99e820a62ac16d0))
* add GetOverloadsLogic with tests ([0e5a89c](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/0e5a89c563cccc8c6310376916f00cc102f94bd0))
* add models for find_obsolete_usage ([91f4799](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/91f47999e16fa92032b32538123462b08992451c))
* add models for get_overloads ([6cc229b](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/6cc229b0221166829a1bf7d5efa4e9704ce96f32))
* register find_obsolete_usage MCP tool ([3f19180](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/3f1918099401be8413f0df6838cef0cdf7bb3a66))
* register get_overloads MCP tool ([b5abd71](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/b5abd714eb045348ee47d8aea0f8f744323fcc1b))


### Bug Fixes

* bypass CS0619 in fixture by using nameof() for IsError obsolete ([b555c79](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/b555c7943cfc6bb87e76d3a89f5feebc491983e0))
* drop [Obsolete(..., true)] fixture, can't suppress CS0619 in MSBuildWorkspace ([38b4e16](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/38b4e16e24bccf4f4666698ef7af059efcac5972))
* handle conditional-access + qualified-name patterns in nested-skip filter ([6bc0c9b](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/6bc0c9b3b8b4b8d4d95b201a0400d9050ed69020))
* handle RefKind.RefReadOnlyParameter, cover all ref kinds in tests ([4fd8ecc](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/4fd8ecc9fb762a0f1e49a9ca258f653cd673bdbf))
* project-level NoWarn for obsolete diagnostics in TestLib fixture ([f0923d7](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/f0923d7fa6253240c30beb35b278a26253dcbb90))
* skip pragma-suppressed diagnostics in get_diagnostics ([c5a1791](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/c5a1791b57f86a38906040f9b7bc025468a81f7b))
* suppress CS0619 in fixture, document test-project filter, add IsGenerated test ([33c3f6b](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/33c3f6b0aa7a3aca387079c0264c709333037d2d))
* trim AdapterProjects to the 3 with PackageReferences ([ace48a2](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/ace48a2c961d1046594c6962fc3be3be5cd89413))

## [1.12.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.11.0...v1.12.0) (2026-04-30)


### Features

* add find_god_objects MCP tool ([9e19921](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/9e19921e0650cd268d37765de010bef5a144297a))


### Bug Fixes

* braced namespaces in fixture, address remaining review issues ([45405af](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/45405af56ecc80e3a32e8922ce344ba58867fe5e))

## [1.11.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.10.0...v1.11.0) (2026-04-30)


### Features

* add get_project_health composite tool ([ea4f001](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/ea4f001f7620f7087a37c02c325c5c87f1c2fc30))
* add GetProjectHealthLogic compositing 7 health dimensions ([e85268c](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/e85268ca4b582fa1e83b58bebd54a4012d53bce1))
* add models for get_project_health ([1d17c7a](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/1d17c7a3e854cced756510edd1068fbd908ed3af))
* register get_project_health MCP tool ([f967c7f](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/f967c7f7495f024cba44cd180b5b641626fff690))


### Bug Fixes

* **deps:** update docusaurus monorepo to v3.10.1 ([1f7d6ae](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/1f7d6ae3c328a5b8839a7619fd88f1bb05421129))
* **deps:** update docusaurus monorepo to v3.10.1 ([dbcb643](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/dbcb643265101322766eb02c2939841c1e2a999b))
* project filter case-insensitive + load-bearing comment ([c51eecb](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/c51eecbf636b8937b6ecb4d7d9e5c09c755864d1))

## [1.10.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.9.0...v1.10.0) (2026-04-30)


### Features

* add find_event_subscribers MCP tool ([976b2e6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/976b2e618ea937f5df33b704f65e6063e689d81b))
* add FindEventSubscribersLogic with += / -= site detection ([2d8bb26](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/2d8bb265156ffab3bb9f72a36b52ece781815504))
* add models for find_event_subscribers ([b43fbb4](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/b43fbb4a5bac13e2ffd470ebfc826d4f8ae2d73d))
* add SymbolResolver.FindEvents helper ([5e7d23d](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/5e7d23d8a7a7e6c5bd25f5fae976c9b9d534bd24))
* register find_event_subscribers MCP tool ([cec3372](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/cec337247e8e884070adc7d104f66090cab6b329))


### Bug Fixes

* avoid angle brackets in tool description so MDX docs build succeeds ([54c1436](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/54c1436f8a68b50b0740f64c9bf36d0e9cfa5530))

## [1.9.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.8.0...v1.9.0) (2026-04-29)


### Features

* add get_call_graph MCP tool ([c5ead9a](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/c5ead9ac92933d2a3c26c1482c58edc4ad3cb79c))
* add GetCallGraphLogic with transitive caller/callee BFS ([a801188](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/a801188037c18e80ff51ffc6232b24668c4c2c66))
* add models for get_call_graph ([ae3e295](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/ae3e29555fccf87a14e8390c2e11900b1eec1a96))
* register get_call_graph MCP tool ([7dc3102](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/7dc3102f9f19a5d4dc190136b33ed3f6473c5baa))


### Bug Fixes

* make WalkCallers async and preserve edges to truncated targets ([3797346](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/3797346f5604fd5a85c01b92c2f1ecc60bc65551))
* only suppress property-get edge for simple assignment, not compound ([34181df](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/34181df7b15c00d43d89b8049eb716985a3b0cac))
* share maxNodes budget and follow implicit-new/ctor-initializer calls ([8826ee8](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/8826ee8e73945817abdd9cd97e023e641b5d8746))


### Performance Improvements

* cache SyntaxTree→Compilation map and thread cancellation through callees walk ([7bcc0a4](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/7bcc0a4e1b0547864f0962b68b28ed2cae7805eb))

## [1.8.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.7.0...v1.8.0) (2026-04-29)


### Features

* add find_breaking_changes MCP tool ([63e6854](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/63e685469a1abc96ff0aa8b5ea2ebb3fa13874cc))
* add FindBreakingChangesLogic with five change-kind diff ([e21059e](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/e21059e64c5c26f6296515828670301eec4bceb9))
* add models for find_breaking_changes output ([0106c93](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/0106c93acdad414cf4ac0fa463575cbfaf865d2c))
* register find_breaking_changes MCP tool ([5e89f89](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/5e89f89de6bb85002582df6d36eca90fe7f1c555))


### Bug Fixes

* collapse duplicate FQNs in Diff to avoid double-reported changes ([78ce7eb](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/78ce7ebc63b5038293adf95b5354d62876368a1f))
* include BCL refs and accept case-insensitive JSON in baseline loaders ([84b5ec2](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/84b5ec28b481473587deaa163d1dbfe376d5e69f))

## [1.7.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.6.0...v1.7.0) (2026-04-29)


### Features

* add get_public_api_surface MCP tool (public + protected API enumeration) ([5f3b3eb](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/5f3b3eba366d634815986ec5dc4221eef1f0e925))
* add GetPublicApiSurfaceLogic with public/protected enumeration ([545368e](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/545368eb20b7f8f4dc7b874d02aed60633fd80ed))
* add models for get_public_api_surface output ([0eb5345](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/0eb53452d5032ba8ced4080190f60b3073bf7f5e))
* register get_public_api_surface MCP tool ([559626f](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/559626ff1423c5e4a9d40c0608d039e47cb3a9f5))


### Bug Fixes

* exclude public-nested-in-internal types and emit parameterised member names ([69282f9](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/69282f9d517c0e52a6aa12a162be4ba21769c9be))

## [1.6.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.5.0...v1.6.0) (2026-04-28)


### Features

* add find_disposable_misuse MCP tool (IDisposable / IAsyncDisposable leak detection) ([8306f44](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/8306f44eaca64873d344acb05567a72403dbe682))
* add FindDisposableMisuseLogic with two pattern detectors ([751108d](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/751108d288947c8cc09c13f084dc62bcb6f114ef))
* add models for find_disposable_misuse output ([f80f9f6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/f80f9f6b5b89fbb197c3878443d9c6dacd4da42c))
* register find_disposable_misuse MCP tool ([5cc5bb6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/5cc5bb661ec85096ac76266a975ba73d0bd91abf))

## [1.5.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.4.0...v1.5.0) (2026-04-28)


### Features

* add find_async_violations MCP tool (sync-over-async, async void, missing await, fire-and-forget) ([5ac2f1a](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/5ac2f1adb9861893c940acedfb7959a7b10d36ca))
* add FindAsyncViolationsLogic with six pattern detectors ([0153007](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/015300710a8d93964b766c537bd8d419c1761269))
* add models for find_async_violations output ([b8cf04b](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/b8cf04be0b10d725cfe1a11cb8e739bad635a2ba))
* register find_async_violations MCP tool ([b336055](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/b336055caefb24c1bc2ad5560ef3046c1e756a5e))


### Bug Fixes

* detect .Result on ValueTask&lt;T&gt;; silence MA0002 ToDictionary warnings ([46c5bf1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/46c5bf197cf11beeee7d2110c02d17582060ab21))
* detect GetResult() on ConfiguredValueTaskAwaitable ([bd0890c](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/bd0890ce702907f4c3a9531fb284a46c9ceff57d))

## [1.4.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.3.0...v1.4.0) (2026-04-28)


### Features

* add find_uncovered_symbols MCP tool (test-coverage report with risk hotspots) ([983bf57](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/983bf57be6df3288d7ad21c26775a09e33dbdaf1))
* add FindUncoveredSymbolsLogic ([4527259](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/4527259fe167b3337f86db667bb95b2a6c6ee3a0))
* add models for find_uncovered_symbols output ([67232e7](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/67232e76b9f7269737f35d4c5c965259c762c0e1))
* register find_uncovered_symbols MCP tool ([9ca45d0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/9ca45d0b8b3ae705292c9d497cb39626f4b79cc7))


### Bug Fixes

* revert virtual-dispatch propagation; tighten Greeter.Greet test predicate ([14cf42b](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/14cf42b6751847a732c549bc368570f5abd4cd33))

## [1.3.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.2.1...v1.3.0) (2026-04-27)


### Features

* add find_tests_for_symbol MCP tool (xUnit/NUnit/MSTest) ([c5252a2](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/c5252a2f7940b072493e17d64946938f4b5792d9))
* add FindTestsForSymbolLogic (direct mode) ([1f8cf9a](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/1f8cf9ad03b56161a3f78528a910fcf988f775a8))
* add TestAttributeRecognizer for xUnit/NUnit/MSTest ([d65a763](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/d65a76384d5b2847c8e2bbefa4fd69996820ff73))
* add TestProjectDetector via package-ref pattern scan ([4199cf6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/4199cf685386c2f28e49dc4fc02be66b7c19ba25))
* add transitive mode to FindTestsForSymbolLogic ([41db467](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/41db467c6c166447235237bb14d648aa5c66b400))
* register find_tests_for_symbol MCP tool ([e15213d](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/e15213d7f9ad1a5ae6a36241478ef2ffa4bc8de7))

## [1.2.1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.2.0...v1.2.1) (2026-04-26)


### Bug Fixes

* enable manifest mode for release-please and sync version files to 1.2.0 ([03d3a8f](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/03d3a8f96d32117eee29f826ed3949cfaf31a6f1))
* enable release-please manifest mode + sync version files to 1.2.0 ([b159d05](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/b159d0517c4ffa60042575d6c761e315e0650eed))

## [1.2.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.7...v1.2.0) (2026-04-25)


### Features

* external-assembly analysis — Phase 1 (Tier 1 + inspect_external_assembly) ([#96](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/96)) ([52b229b](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/52b229baf9e1585d61d2903c3f8b4ceca713b19f))
* external-assembly analysis — Phase 2 (Tier-2 references) ([#98](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/98)) ([a078981](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/a07898114d8e65dd91f312a04de9bccf17d8c215))
* external-assembly analysis — Phase 3 (peek_il) ([#100](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/100)) ([175d9aa](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/175d9aa85ea1f0c352f06d6114a511490051fcaa))


### Performance Improvements

* fix find_callers and find_attribute_usages regressions, add missing benchmarks ([8422cb9](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/8422cb91eb977fd63b4574a54110fd984e597a5f))
* skip FindImplementationForInterfaceMember when method names differ ([7d137a3](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/7d137a34a9cf8ad1b8e7046846800a6a903b5b3e))
* use default ToDisplayString() in FindAttributeUsagesLogic.BuildResults ([f64dee4](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/f64dee4513009a3162af546c05aa4ef6591cc8d3))

## [1.1.7](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.6...v1.1.7) (2026-03-30)


### Bug Fixes

* set server.json version to match release-please manifest ([6f35202](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/6f35202b7f2e7d37fb1e10f387239b8f91e0aaa6))
* set server.json version to match release-please manifest ([7898409](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/789840973ed3de57fe12e6be2a313b0f99f4b5da))

## [1.1.6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.5...v1.1.6) (2026-03-30)


### Bug Fixes

* reset server.json version for release-please management ([0dad708](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/0dad708f1efd42f9f5e78f3b41c9f1827b1a81b6))
* sync server.json version with latest NuGet release ([55b80a8](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/55b80a85982ee13e8a86ceb447d327515d0acd6a))

## [1.1.5](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.4...v1.1.5) (2026-03-29)


### Bug Fixes

* include server.json in release-please version bumps ([3812bb1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/3812bb1c2eef691423c258bd5bb6de112c050f0c))
* include server.json in release-please version bumps ([5598f53](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/5598f5394a4fa7512f2a2303f63b7181bc20eb97))

## [1.1.4](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.3...v1.1.4) (2026-03-29)


### Bug Fixes

* restore test fixtures in release workflows ([6924b03](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/6924b0355eefde8f79ffc635e94aafd24f5a1bf0))
* restore test fixtures in release workflows ([ba8e53c](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/ba8e53c0e3ea294909a0533597bff23be00bf5e8))

## [1.1.3](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.2...v1.1.3) (2026-03-29)


### Bug Fixes

* add missing DI package reference to test fixture TestLib2 ([33f9279](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/33f92793632257e9648dbd182bc9511f1ca86671))
* add missing DI package reference to test fixture TestLib2 ([9940c1c](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/9940c1c175f685a0b2aa94603947abfd4b23ef3c))

## [1.1.2](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.1...v1.1.2) (2026-03-28)


### Bug Fixes

* add mcp-name to README for MCP registry ownership verification ([679c4e6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/679c4e69f5911af5c5ce547acb4c9a3eebccf38e))
* add mcp-name to README for MCP registry ownership verification ([cb4acdc](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/cb4acdcf296d3f1a0c433e9e7583092c1ede372d))

## [1.1.1](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.1.0...v1.1.1) (2026-03-28)


### Bug Fixes

* correct MCP registry name casing ([7770bbe](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/7770bbe191ce7a30edf17a5055d36b9c1b510d5a))
* correct MCP registry name casing ([ad84450](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/ad844503043fda4a77af9b92c4eab61e3412f28e))

## [1.1.0](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/compare/v1.0.15...v1.1.0) (2026-03-24)


### Features

* add load_solution and unload_solution (fixed) ([745925f](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/745925f10ba251663578e52a490f79fbe190daae))


### Bug Fixes

* address PR [#37](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/37) review feedback for load/unload solution ([cfd0039](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/cfd0039e3f8bc7a916db6161ee6cdbe7040fb02b))
* correct dotnet-version format (10.0.x not net10.0) ([0bb9c27](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/0bb9c27452c2e6c7450b48f1d4654e2cd4db7a96))
* restore test fixture solution before build in CI ([8b155d6](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/commit/8b155d65251ab48f2a5ae36656ed2402c9d6d03e))
