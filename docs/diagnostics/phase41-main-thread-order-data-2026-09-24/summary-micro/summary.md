Per-process local microbenchmarks. All values below are elapsed us per call.

Each version uses 16 retained batch means; paired savings use 8 same-round differences. Positive savings indicate a lower candidate time. Processes and warmup are separate; empty batches are not subtracted.

| Run | Backend | Probe | Case | Order / cases | Baseline median | Candidate median | Saving % | Paired round median saving |
|---|---|---|---|---|---:|---:|---:|---:|
| player00-66233b83 | IL2CPP | axis-mass | box | ABBA / box,character | 0.553760 | 0.239941 | 56.67 | 0.317529 |
| player00-66233b83 | IL2CPP | axis-mass | character | ABBA / box,character | 0.566064 | 0.239795 | 57.64 | 0.333862 |
| player00-66233b83 | IL2CPP | native-bookkeeping | box | ABBA / box,character | 0.558008 | 0.137891 | 75.29 | 0.420703 |
| player00-66233b83 | IL2CPP | native-bookkeeping | character | ABBA / box,character | 0.554688 | 0.142432 | 74.32 | 0.411914 |
| player01-a923265f | IL2CPP | axis-mass | box | ABBA / character,box | 0.555518 | 0.242041 | 56.43 | 0.313867 |
| player01-a923265f | IL2CPP | axis-mass | character | ABBA / character,box | 0.567627 | 0.241895 | 57.38 | 0.323413 |
| player01-a923265f | IL2CPP | native-bookkeeping | box | ABBA / character,box | 0.555664 | 0.135791 | 75.56 | 0.420020 |
| player01-a923265f | IL2CPP | native-bookkeeping | character | ABBA / character,box | 0.558203 | 0.141553 | 74.64 | 0.416211 |
| player10-f0ebe111 | IL2CPP | axis-mass | box | BAAB / box,character | 0.551562 | 0.239844 | 56.52 | 0.311401 |
| player10-f0ebe111 | IL2CPP | axis-mass | character | BAAB / box,character | 0.568066 | 0.240234 | 57.71 | 0.327661 |
| player10-f0ebe111 | IL2CPP | native-bookkeeping | box | BAAB / box,character | 0.557373 | 0.137939 | 75.25 | 0.420044 |
| player10-f0ebe111 | IL2CPP | native-bookkeeping | character | BAAB / box,character | 0.554980 | 0.143506 | 74.14 | 0.411743 |
| player11-6bad76e3 | IL2CPP | axis-mass | box | BAAB / character,box | 0.546875 | 0.240820 | 55.96 | 0.305664 |
| player11-6bad76e3 | IL2CPP | axis-mass | character | BAAB / character,box | 0.564307 | 0.240430 | 57.39 | 0.323999 |
| player11-6bad76e3 | IL2CPP | native-bookkeeping | box | BAAB / character,box | 0.554932 | 0.136621 | 75.38 | 0.415771 |
| player11-6bad76e3 | IL2CPP | native-bookkeeping | character | BAAB / character,box | 0.554980 | 0.142285 | 74.36 | 0.411572 |

Invalid runs (retained as evidence, excluded from this table): none.

Raw CSVs, environment, process/command records and hashes are preserved under raw/. See summary.json for every round, calibration, validation and source fingerprint. Warmup batches were not saved by the current harnesses. These are descriptive results, not full Request/Final handoff measurements.
