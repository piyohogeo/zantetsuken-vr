# Paired mass — 同一process ABBA比較

- Unity Editor MonoのMain上のStopwatch経過時間。CPU実行時間、IL2CPP Player性能、切断・公開全体の時間ではない。
- 箱・Characterごとにbaseline/candidate各1 batchのwarmup後、ABBAを8 round。各batchは1024回のTryDivide。setup、出力一致assert、teardown、保存は計測外。
- 各roundのbaseline値はorder 0/3の2 sample平均、candidate値はorder 1/2の2 sample平均。paired差はそのcandidate平均−baseline平均。負が短縮。
- runの主指標は8個のround paired差の中央値。百分率はroundごとにpaired差/baseline平均を計算した8値の中央値で、中央値同士の比率ではない。
- baseline/candidateのrun中央値はそれぞれ16 batch平均値の中央値。その2中央値の差は別指標であり、paired差の中央値とは同一視しない。
- 2 process代表値は各run集約値の中央値、rangeはrun集約値のmin–max。process間でsampleをpoolしない。2 processだけで有意差・実Player効果を確定しない。
- 全128 sampleを保持し、外れ値除外・失敗runの自動除外を行わない。GC測定・強制GCなし。反復中の割込みやGC等による経過時間変動を含み得る。
- baselineは8bae0b1eの型名だけを置換した実装、candidateは現行ProvisionalBoxMass。同じshape入力で全出力のbit一致assertを測定前後に行うハーネスの局所比較。

## 2 process代表値

主指標は各runのround paired差中央値を2 runで集約した値。

| case | paired差中央値 us | run範囲 us | paired差 % | baseline batch中央値 us | candidate batch中央値 us |
|---|---:|---:|---:|---:|---:|
| box | -0.105823 | -0.182446–-0.029199 | -2.292739 | 4.549243 | 4.303442 |
| character | -0.028345 | -0.031714–-0.024976 | -0.627338 | 4.406958 | 4.383936 |

各version代表値のprocess間範囲。paired差とは独立の記述統計。

| case | baseline run中央値 min–max us | candidate run中央値 min–max us | version代表値の差 us |
|---|---:|---:|---:|
| box | 3.969141–5.129346 | 3.948437–4.658447 | -0.245801 |
| character | 4.016797–4.797119 | 3.911621–4.856250 | -0.023022 |

## Process別

| run | case | paired差中央値 us | paired差 % | baseline batch中央値 us | candidate batch中央値 us | 2中央値の差 us |
|---|---|---:|---:|---:|---:|---:|
| run1-paired-mass | box | -0.182446 | -3.847136 | 5.129346 | 4.658447 | -0.470898 |
| run1-paired-mass | character | -0.031714 | -0.618251 | 4.797119 | 4.856250 | 0.059131 |
| run2-paired-mass | box | -0.029199 | -0.738342 | 3.969141 | 3.948437 | -0.020703 |
| run2-paired-mass | character | -0.024976 | -0.636425 | 4.016797 | 3.911621 | -0.105176 |

## Round別paired差

| run | case | round | baseline 2sample平均 us | candidate 2sample平均 us | paired差 us | paired差 % |
|---|---|---:|---:|---:|---:|---:|
| run1-paired-mass | box | 0 | 5.129346 | 4.790869 | -0.338477 | -6.598825 |
| run1-paired-mass | box | 1 | 4.680078 | 4.600098 | -0.079980 | -1.708956 |
| run1-paired-mass | box | 2 | 4.713867 | 4.578027 | -0.135840 | -2.881707 |
| run1-paired-mass | box | 3 | 4.759473 | 4.530420 | -0.229053 | -4.812565 |
| run1-paired-mass | box | 4 | 5.063037 | 4.576758 | -0.486279 | -9.604498 |
| run1-paired-mass | box | 5 | 6.946387 | 6.851904 | -0.094482 | -1.360166 |
| run1-paired-mass | box | 6 | 5.792139 | 5.309277 | -0.482861 | -8.336495 |
| run1-paired-mass | box | 7 | 5.421973 | 5.504395 | 0.082422 | 1.520146 |
| run1-paired-mass | character | 0 | 4.483008 | 4.665039 | 0.182031 | 4.060471 |
| run1-paired-mass | character | 1 | 4.686230 | 4.708838 | 0.022607 | 0.482422 |
| run1-paired-mass | character | 2 | 4.524805 | 4.634863 | 0.110059 | 2.432339 |
| run1-paired-mass | character | 3 | 4.721289 | 4.540869 | -0.180420 | -3.821412 |
| run1-paired-mass | character | 4 | 4.797119 | 4.863232 | 0.066113 | 1.378187 |
| run1-paired-mass | character | 5 | 4.975586 | 4.889453 | -0.086133 | -1.731109 |
| run1-paired-mass | character | 6 | 5.005176 | 4.919141 | -0.086035 | -1.718924 |
| run1-paired-mass | character | 7 | 5.280273 | 5.070020 | -0.210254 | -3.981875 |
| run2-paired-mass | box | 0 | 3.952637 | 3.956738 | 0.004102 | 0.103768 |
| run2-paired-mass | box | 1 | 3.975830 | 3.939209 | -0.036621 | -0.921093 |
| run2-paired-mass | box | 2 | 4.079150 | 3.979004 | -0.100146 | -2.455082 |
| run2-paired-mass | box | 3 | 3.953564 | 3.932471 | -0.021094 | -0.533538 |
| run2-paired-mass | box | 4 | 4.030176 | 3.946143 | -0.084033 | -2.085100 |
| run2-paired-mass | box | 5 | 3.938818 | 3.963184 | 0.024365 | 0.618592 |
| run2-paired-mass | box | 6 | 4.039502 | 3.906445 | -0.133057 | -3.293887 |
| run2-paired-mass | box | 7 | 3.919678 | 3.897900 | -0.021777 | -0.555590 |
| run2-paired-mass | character | 0 | 4.280225 | 4.378809 | 0.098584 | 2.303243 |
| run2-paired-mass | character | 1 | 4.179932 | 4.232617 | 0.052686 | 1.260440 |
| run2-paired-mass | character | 2 | 4.046484 | 4.045605 | -0.000879 | -0.021720 |
| run2-paired-mass | character | 3 | 4.011475 | 3.890381 | -0.121094 | -3.018684 |
| run2-paired-mass | character | 4 | 3.928809 | 3.903320 | -0.025488 | -0.648753 |
| run2-paired-mass | character | 5 | 4.044629 | 3.975879 | -0.068750 | -1.699785 |
| run2-paired-mass | character | 6 | 3.919727 | 3.895264 | -0.024463 | -0.624097 |
| run2-paired-mass | character | 7 | 3.958643 | 3.871338 | -0.087305 | -2.205420 |

全128測定行をpaired-summary.jsonのraw_rowsへ保持。元CSVは変更していない。
