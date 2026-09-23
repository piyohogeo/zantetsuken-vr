# Main thread optimization — Editor Mono局所比較

入力測定行: 5760（全行をsummary.jsonのraw_rowsに保持）。

- 時刻はUnity Editor MonoのMain上のStopwatch経過時間。CPU実行時間、IL2CPP Player性能、切断全体の時間ではない。Ready・割込み・GC等を含み得る。
- 登録はprivate IsRegisteredのdelegate呼出しだけ。storage入力検証、空slot探索、登録・retire更新、Geometry Commit全体を含まない。setup/teardownは計測外。
- 登録live16/32は代表規模、live512とhighwater2048_live16はstress。各scopeは1 batch warmup後8 sample、1 sample=4096 lookupの平均。
- shape_pair_create_disposeは正負shape生成とDisposeを含む256反復平均、box_mass_divideは質量分割1024反復平均。各1 batch warmup後8 sample。
- provisional_build/prepare_colliders/adopt_collidersは各64回の単発経過時間。先行4 pairはwarmup。Actorは非活性、Destroy/Withdrawおよびcook準備・完了は対象区間外。公開やFinal handoff全体は測っていない。
- 全時間を1 iterationあたりusへ換算。p95は各run内のsample値をtype 7線形補間で算出。8 batch平均のp95は個々のcallの95パーセンタイルではない。
- variant代表値は2 processのrun中央値の中央値。rangeはrun中央値のmin–max。異なるprocessのsampleを混ぜず、子区間の中央値も足さない。
- 差分はcandidate代表値−baseline代表値。負が短縮。独立2 processの記述比較であり、有意差・Player改善・因果効果を確定するものではない。baseline自身のrun間変動も併記する。
- 4096 B配列allocation probeへ応答しないcounterのbytesはunavailable。0を無割当の証明に使わず、ソース上の割当削減とも区別する。
- 個別修正の差分は対象metricだけ: o1=登録、view=shape生成/破棄とprovisional build、colliders=prepare/adopt、mass=mass divideとprovisional build。allのみ全metricを比較。対象外の行も原文とrun集約を保持する。

## Baselineのprocess間変動

| metric | run中央値の中央値 us | run中央値 min–max us | spread us | spread % |
|---|---:|---:|---:|---:|
| physics/box/adopt_colliders | 2.950000 | 2.700000–3.200000 | 0.500000 | 16.949153 |
| physics/box/box_mass_divide | 4.140186 | 3.861230–4.419141 | 0.557910 | 13.475487 |
| physics/box/prepare_colliders | 4.900000 | 4.300000–5.500000 | 1.200000 | 24.489796 |
| physics/box/provisional_build | 40.050000 | 35.950000–44.150000 | 8.200000 | 20.474407 |
| physics/box/shape_pair_create_dispose | 1.921582 | 1.753516–2.089648 | 0.336133 | 17.492504 |
| physics/character/adopt_colliders | 12.900000 | 10.400000–15.400000 | 5.000000 | 38.759690 |
| physics/character/box_mass_divide | 4.230078 | 3.841211–4.618945 | 0.777734 | 18.385816 |
| physics/character/prepare_colliders | 23.200000 | 18.400000–28.000000 | 9.600000 | 41.379310 |
| physics/character/provisional_build | 90.200000 | 73.500000–106.900000 | 33.400000 | 37.028825 |
| physics/character/shape_pair_create_dispose | 5.960352 | 5.449219–6.471484 | 1.022266 | 17.151096 |
| registration/highwater2048_live16/late_duplicate | 2.063800 | 2.044983–2.082617 | 0.037634 | 1.823543 |
| registration/highwater2048_live16/unregistered | 2.009381 | 1.941138–2.077625 | 0.136487 | 6.792480 |
| registration/live16/late_duplicate | 0.025830 | 0.020801–0.030859 | 0.010059 | 38.941399 |
| registration/live16/unregistered | 0.040662 | 0.020886–0.060437 | 0.039551 | 97.268088 |
| registration/live32/late_duplicate | 0.061060 | 0.044690–0.077429 | 0.032739 | 53.618553 |
| registration/live32/unregistered | 0.058875 | 0.049231–0.068518 | 0.019287 | 32.759693 |
| registration/live512/late_duplicate | 0.752209 | 0.695361–0.809058 | 0.113696 | 15.114977 |
| registration/live512/unregistered | 0.814612 | 0.690247–0.938977 | 0.248730 | 30.533619 |

## 対象metricの比較

差分の分母・基準は同じmetricのbaseline process中央値の中央値。時間は1 iterationあたり。

| variant | metric | role | median us | run中央値 min–max us | delta us | delta % | bytes/iteration |
|---|---|---|---:|---:|---:|---:|---:|
| all | physics/box/adopt_colliders | representative | 2.650000 | 2.500000–2.800000 | -0.300000 | -10.169492 | unavailable |
| all | physics/box/box_mass_divide | representative | 3.956323 | 3.722998–4.189648 | -0.183862 | -4.440919 | unavailable |
| all | physics/box/prepare_colliders | representative | 4.500000 | 4.300000–4.700000 | -0.400000 | -8.163265 | unavailable |
| all | physics/box/provisional_build | representative | 35.275000 | 34.000000–36.550000 | -4.775000 | -11.922597 | unavailable |
| all | physics/box/shape_pair_create_dispose | representative | 1.955469 | 1.449805–2.461133 | 0.033887 | 1.763480 | unavailable |
| all | physics/character/adopt_colliders | representative | 10.550000 | 10.300000–10.800000 | -2.350000 | -18.217054 | unavailable |
| all | physics/character/box_mass_divide | representative | 4.106860 | 3.960303–4.253418 | -0.123218 | -2.912896 | unavailable |
| all | physics/character/prepare_colliders | representative | 18.975000 | 18.850000–19.100000 | -4.225000 | -18.211207 | unavailable |
| all | physics/character/provisional_build | representative | 70.725000 | 69.300000–72.150000 | -19.475000 | -21.590909 | unavailable |
| all | physics/character/shape_pair_create_dispose | representative | 3.326074 | 3.127539–3.524609 | -2.634277 | -44.196677 | unavailable |
| all | registration/highwater2048_live16/late_duplicate | stress | 0.002130 | 0.001904–0.002356 | -2.061670 | -99.896786 | unavailable |
| all | registration/highwater2048_live16/unregistered | stress | 0.002014 | 0.001904–0.002124 | -2.007367 | -99.899762 | unavailable |
| all | registration/live16/late_duplicate | representative | 0.002106 | 0.001904–0.002307 | -0.023724 | -91.847826 | unavailable |
| all | registration/live16/unregistered | representative | 0.001917 | 0.001733–0.002100 | -0.038745 | -95.286701 | unavailable |
| all | registration/live32/late_duplicate | representative | 0.002057 | 0.001904–0.002209 | -0.059003 | -96.631347 | unavailable |
| all | registration/live32/unregistered | representative | 0.001990 | 0.001904–0.002075 | -0.056885 | -96.620361 | unavailable |
| all | registration/live512/late_duplicate | stress | 0.002106 | 0.001904–0.002307 | -0.750104 | -99.720063 | unavailable |
| all | registration/live512/unregistered | stress | 0.001996 | 0.001892–0.002100 | -0.812616 | -99.754994 | unavailable |
| colliders | physics/box/adopt_colliders | representative | 2.650000 | 2.600000–2.700000 | -0.300000 | -10.169492 | unavailable |
| colliders | physics/box/prepare_colliders | representative | 4.400000 | 4.300000–4.500000 | -0.500000 | -10.204082 | unavailable |
| colliders | physics/character/adopt_colliders | representative | 10.400000 | 10.400000–10.400000 | -2.500000 | -19.379845 | unavailable |
| colliders | physics/character/prepare_colliders | representative | 18.925000 | 18.700000–19.150000 | -4.275000 | -18.426724 | unavailable |
| mass | physics/box/box_mass_divide | representative | 3.922388 | 3.798486–4.046289 | -0.217798 | -5.260582 | unavailable |
| mass | physics/box/provisional_build | representative | 39.850000 | 37.000000–42.700000 | -0.200000 | -0.499376 | unavailable |
| mass | physics/character/box_mass_divide | representative | 4.413232 | 4.330371–4.496094 | 0.183154 | 4.329809 | unavailable |
| mass | physics/character/provisional_build | representative | 76.200000 | 73.750000–78.650000 | -14.000000 | -15.521064 | unavailable |
| o1 | registration/highwater2048_live16/late_duplicate | stress | 0.002002 | 0.001904–0.002100 | -2.061798 | -99.902997 | unavailable |
| o1 | registration/highwater2048_live16/unregistered | stress | 0.001807 | 0.001709–0.001904 | -2.007574 | -99.910090 | unavailable |
| o1 | registration/live16/late_duplicate | representative | 0.001984 | 0.001904–0.002063 | -0.023846 | -92.320416 | unavailable |
| o1 | registration/live16/unregistered | representative | 0.001782 | 0.001709–0.001855 | -0.038879 | -95.616932 | unavailable |
| o1 | registration/live32/late_duplicate | representative | 0.001978 | 0.001904–0.002051 | -0.059082 | -96.761295 | unavailable |
| o1 | registration/live32/unregistered | representative | 0.001776 | 0.001709–0.001843 | -0.057098 | -96.983205 | unavailable |
| o1 | registration/live512/late_duplicate | stress | 0.001984 | 0.001904–0.002063 | -0.750226 | -99.736291 | unavailable |
| o1 | registration/live512/unregistered | stress | 0.001782 | 0.001709–0.001855 | -0.812830 | -99.781218 | unavailable |
| view | physics/box/provisional_build | representative | 36.525000 | 34.750000–38.300000 | -3.525000 | -8.801498 | unavailable |
| view | physics/box/shape_pair_create_dispose | representative | 1.226074 | 1.183984–1.268164 | -0.695508 | -36.194542 | unavailable |
| view | physics/character/provisional_build | representative | 78.275000 | 69.300000–87.250000 | -11.925000 | -13.220621 | unavailable |
| view | physics/character/shape_pair_create_dispose | representative | 3.766211 | 3.142188–4.390234 | -2.194141 | -36.812269 | unavailable |

## 各processの集計

個別変更の対象外metricも観測値として保持し、改善差分には使用しない。

| run | metric | samples | median us | p95 us | 比較対象 | allocation |
|---|---|---:|---:|---:|---|---|
| run01-baseline | physics/box/adopt_colliders | 64 | 2.700000 | 2.800000 | yes | unavailable |
| run01-baseline | physics/box/box_mass_divide | 8 | 3.861230 | 3.955796 | yes | unavailable |
| run01-baseline | physics/box/prepare_colliders | 64 | 4.300000 | 5.285000 | yes | unavailable |
| run01-baseline | physics/box/provisional_build | 64 | 35.950000 | 38.140000 | yes | unavailable |
| run01-baseline | physics/box/shape_pair_create_dispose | 8 | 1.753516 | 1.795137 | yes | unavailable |
| run01-baseline | physics/character/adopt_colliders | 64 | 10.400000 | 11.385000 | yes | unavailable |
| run01-baseline | physics/character/box_mass_divide | 8 | 3.841211 | 3.890264 | yes | unavailable |
| run01-baseline | physics/character/prepare_colliders | 64 | 18.400000 | 22.870000 | yes | unavailable |
| run01-baseline | physics/character/provisional_build | 64 | 73.500000 | 86.515000 | yes | unavailable |
| run01-baseline | physics/character/shape_pair_create_dispose | 8 | 5.449219 | 5.813672 | yes | unavailable |
| run01-baseline | registration/highwater2048_live16/late_duplicate | 8 | 2.082617 | 2.198453 | yes | unavailable |
| run01-baseline | registration/highwater2048_live16/unregistered | 8 | 1.941138 | 2.095682 | yes | unavailable |
| run01-baseline | registration/live16/late_duplicate | 8 | 0.020801 | 0.025521 | yes | unavailable |
| run01-baseline | registration/live16/unregistered | 8 | 0.020886 | 0.020914 | yes | unavailable |
| run01-baseline | registration/live32/late_duplicate | 8 | 0.044690 | 0.051542 | yes | unavailable |
| run01-baseline | registration/live32/unregistered | 8 | 0.049231 | 0.050841 | yes | unavailable |
| run01-baseline | registration/live512/late_duplicate | 8 | 0.695361 | 0.700336 | yes | unavailable |
| run01-baseline | registration/live512/unregistered | 8 | 0.690247 | 0.698754 | yes | unavailable |
| run02-o1 | physics/box/adopt_colliders | 64 | 2.600000 | 2.800000 | no | unavailable |
| run02-o1 | physics/box/box_mass_divide | 8 | 3.795947 | 3.943457 | no | unavailable |
| run02-o1 | physics/box/prepare_colliders | 64 | 4.300000 | 5.200000 | no | unavailable |
| run02-o1 | physics/box/provisional_build | 64 | 35.050000 | 37.855000 | no | unavailable |
| run02-o1 | physics/box/shape_pair_create_dispose | 8 | 1.829492 | 2.266465 | no | unavailable |
| run02-o1 | physics/character/adopt_colliders | 64 | 10.600000 | 11.600000 | no | unavailable |
| run02-o1 | physics/character/box_mass_divide | 8 | 3.726562 | 3.789219 | no | unavailable |
| run02-o1 | physics/character/prepare_colliders | 64 | 19.150000 | 21.685000 | no | unavailable |
| run02-o1 | physics/character/provisional_build | 64 | 73.450000 | 97.915000 | no | unavailable |
| run02-o1 | physics/character/shape_pair_create_dispose | 8 | 5.491797 | 5.603418 | no | unavailable |
| run02-o1 | registration/highwater2048_live16/late_duplicate | 8 | 0.001904 | 0.001920 | yes | unavailable |
| run02-o1 | registration/highwater2048_live16/unregistered | 8 | 0.001709 | 0.001709 | yes | unavailable |
| run02-o1 | registration/live16/late_duplicate | 8 | 0.001904 | 0.001904 | yes | unavailable |
| run02-o1 | registration/live16/unregistered | 8 | 0.001709 | 0.001733 | yes | unavailable |
| run02-o1 | registration/live32/late_duplicate | 8 | 0.001904 | 0.001920 | yes | unavailable |
| run02-o1 | registration/live32/unregistered | 8 | 0.001709 | 0.001733 | yes | unavailable |
| run02-o1 | registration/live512/late_duplicate | 8 | 0.001904 | 0.001904 | yes | unavailable |
| run02-o1 | registration/live512/unregistered | 8 | 0.001709 | 0.001733 | yes | unavailable |
| run03-view | physics/box/adopt_colliders | 64 | 2.700000 | 3.000000 | no | unavailable |
| run03-view | physics/box/box_mass_divide | 8 | 3.902051 | 4.043164 | no | unavailable |
| run03-view | physics/box/prepare_colliders | 64 | 4.400000 | 5.940000 | no | unavailable |
| run03-view | physics/box/provisional_build | 64 | 34.750000 | 39.235000 | yes | unavailable |
| run03-view | physics/box/shape_pair_create_dispose | 8 | 1.183984 | 1.213809 | yes | unavailable |
| run03-view | physics/character/adopt_colliders | 64 | 10.500000 | 10.885000 | no | unavailable |
| run03-view | physics/character/box_mass_divide | 8 | 3.914111 | 3.958843 | no | unavailable |
| run03-view | physics/character/prepare_colliders | 64 | 18.900000 | 21.840000 | no | unavailable |
| run03-view | physics/character/provisional_build | 64 | 69.300000 | 79.380000 | yes | unavailable |
| run03-view | physics/character/shape_pair_create_dispose | 8 | 3.142188 | 3.158125 | yes | unavailable |
| run03-view | registration/highwater2048_live16/late_duplicate | 8 | 1.934338 | 2.076019 | no | unavailable |
| run03-view | registration/highwater2048_live16/unregistered | 8 | 2.026794 | 2.046166 | no | unavailable |
| run03-view | registration/live16/late_duplicate | 8 | 0.020459 | 0.022284 | no | unavailable |
| run03-view | registration/live16/unregistered | 8 | 0.020483 | 0.022889 | no | unavailable |
| run03-view | registration/live32/late_duplicate | 8 | 0.048120 | 0.048726 | no | unavailable |
| run03-view | registration/live32/unregistered | 8 | 0.048169 | 0.070090 | no | unavailable |
| run03-view | registration/live512/late_duplicate | 8 | 0.654980 | 0.668488 | no | unavailable |
| run03-view | registration/live512/unregistered | 8 | 0.649231 | 0.665853 | no | unavailable |
| run04-colliders | physics/box/adopt_colliders | 64 | 2.600000 | 2.700000 | yes | unavailable |
| run04-colliders | physics/box/box_mass_divide | 8 | 3.742725 | 4.003867 | no | unavailable |
| run04-colliders | physics/box/prepare_colliders | 64 | 4.300000 | 4.985000 | yes | unavailable |
| run04-colliders | physics/box/provisional_build | 64 | 34.900000 | 37.400000 | no | unavailable |
| run04-colliders | physics/box/shape_pair_create_dispose | 8 | 1.690430 | 1.825566 | no | unavailable |
| run04-colliders | physics/character/adopt_colliders | 64 | 10.400000 | 10.870000 | yes | unavailable |
| run04-colliders | physics/character/box_mass_divide | 8 | 3.755713 | 3.820752 | no | unavailable |
| run04-colliders | physics/character/prepare_colliders | 64 | 18.700000 | 20.500000 | yes | unavailable |
| run04-colliders | physics/character/provisional_build | 64 | 73.000000 | 83.685000 | no | unavailable |
| run04-colliders | physics/character/shape_pair_create_dispose | 8 | 5.241992 | 5.488047 | no | unavailable |
| run04-colliders | registration/highwater2048_live16/late_duplicate | 8 | 2.216650 | 2.255762 | no | unavailable |
| run04-colliders | registration/highwater2048_live16/unregistered | 8 | 1.871484 | 1.989093 | no | unavailable |
| run04-colliders | registration/live16/late_duplicate | 8 | 0.020386 | 0.020402 | no | unavailable |
| run04-colliders | registration/live16/unregistered | 8 | 0.020178 | 0.028384 | no | unavailable |
| run04-colliders | registration/live32/late_duplicate | 8 | 0.048291 | 0.048356 | no | unavailable |
| run04-colliders | registration/live32/unregistered | 8 | 0.048206 | 0.049642 | no | unavailable |
| run04-colliders | registration/live512/late_duplicate | 8 | 0.650134 | 0.677010 | no | unavailable |
| run04-colliders | registration/live512/unregistered | 8 | 0.646338 | 0.666234 | no | unavailable |
| run05-mass | physics/box/adopt_colliders | 64 | 3.100000 | 3.600000 | no | unavailable |
| run05-mass | physics/box/box_mass_divide | 8 | 4.046289 | 4.106641 | yes | unavailable |
| run05-mass | physics/box/prepare_colliders | 64 | 5.550000 | 7.055000 | no | unavailable |
| run05-mass | physics/box/provisional_build | 64 | 42.700000 | 49.560000 | yes | unavailable |
| run05-mass | physics/box/shape_pair_create_dispose | 8 | 2.007422 | 2.050195 | no | unavailable |
| run05-mass | physics/character/adopt_colliders | 64 | 11.400000 | 11.900000 | no | unavailable |
| run05-mass | physics/character/box_mass_divide | 8 | 4.496094 | 4.591616 | yes | unavailable |
| run05-mass | physics/character/prepare_colliders | 64 | 20.600000 | 21.700000 | no | unavailable |
| run05-mass | physics/character/provisional_build | 64 | 78.650000 | 81.270000 | yes | unavailable |
| run05-mass | physics/character/shape_pair_create_dispose | 8 | 6.231055 | 7.065918 | no | unavailable |
| run05-mass | registration/highwater2048_live16/late_duplicate | 8 | 1.926855 | 2.022697 | no | unavailable |
| run05-mass | registration/highwater2048_live16/unregistered | 8 | 1.927112 | 2.030726 | no | unavailable |
| run05-mass | registration/live16/late_duplicate | 8 | 0.025098 | 0.025146 | no | unavailable |
| run05-mass | registration/live16/unregistered | 8 | 0.025293 | 0.034604 | no | unavailable |
| run05-mass | registration/live32/late_duplicate | 8 | 0.060425 | 0.060919 | no | unavailable |
| run05-mass | registration/live32/unregistered | 8 | 0.059338 | 0.060243 | no | unavailable |
| run05-mass | registration/live512/late_duplicate | 8 | 0.771680 | 0.791576 | no | unavailable |
| run05-mass | registration/live512/unregistered | 8 | 0.806409 | 0.838778 | no | unavailable |
| run06-all | physics/box/adopt_colliders | 64 | 2.500000 | 2.700000 | yes | unavailable |
| run06-all | physics/box/box_mass_divide | 8 | 3.722998 | 3.809819 | yes | unavailable |
| run06-all | physics/box/prepare_colliders | 64 | 4.300000 | 4.700000 | yes | unavailable |
| run06-all | physics/box/provisional_build | 64 | 34.000000 | 37.155000 | yes | unavailable |
| run06-all | physics/box/shape_pair_create_dispose | 8 | 2.461133 | 3.796191 | yes | unavailable |
| run06-all | physics/character/adopt_colliders | 64 | 10.300000 | 12.905000 | yes | unavailable |
| run06-all | physics/character/box_mass_divide | 8 | 3.960303 | 4.573945 | yes | unavailable |
| run06-all | physics/character/prepare_colliders | 64 | 18.850000 | 21.585000 | yes | unavailable |
| run06-all | physics/character/provisional_build | 64 | 69.300000 | 87.490000 | yes | unavailable |
| run06-all | physics/character/shape_pair_create_dispose | 8 | 3.127539 | 3.513164 | yes | unavailable |
| run06-all | registration/highwater2048_live16/late_duplicate | 8 | 0.001904 | 0.001904 | yes | unavailable |
| run06-all | registration/highwater2048_live16/unregistered | 8 | 0.001904 | 0.001904 | yes | unavailable |
| run06-all | registration/live16/late_duplicate | 8 | 0.001904 | 0.001904 | yes | unavailable |
| run06-all | registration/live16/unregistered | 8 | 0.001733 | 0.001929 | yes | unavailable |
| run06-all | registration/live32/late_duplicate | 8 | 0.001904 | 0.008188 | yes | unavailable |
| run06-all | registration/live32/unregistered | 8 | 0.001904 | 0.002856 | yes | unavailable |
| run06-all | registration/live512/late_duplicate | 8 | 0.001904 | 0.001904 | yes | unavailable |
| run06-all | registration/live512/unregistered | 8 | 0.001892 | 0.001904 | yes | unavailable |
| run07-all | physics/box/adopt_colliders | 64 | 2.800000 | 3.770000 | yes | unavailable |
| run07-all | physics/box/box_mass_divide | 8 | 4.189648 | 4.472085 | yes | unavailable |
| run07-all | physics/box/prepare_colliders | 64 | 4.700000 | 6.055000 | yes | unavailable |
| run07-all | physics/box/provisional_build | 64 | 36.550000 | 55.570000 | yes | unavailable |
| run07-all | physics/box/shape_pair_create_dispose | 8 | 1.449805 | 1.470703 | yes | unavailable |
| run07-all | physics/character/adopt_colliders | 64 | 10.800000 | 11.300000 | yes | unavailable |
| run07-all | physics/character/box_mass_divide | 8 | 4.253418 | 4.389678 | yes | unavailable |
| run07-all | physics/character/prepare_colliders | 64 | 19.100000 | 21.380000 | yes | unavailable |
| run07-all | physics/character/provisional_build | 64 | 72.150000 | 77.890000 | yes | unavailable |
| run07-all | physics/character/shape_pair_create_dispose | 8 | 3.524609 | 3.568555 | yes | unavailable |
| run07-all | registration/highwater2048_live16/late_duplicate | 8 | 0.002356 | 0.002368 | yes | unavailable |
| run07-all | registration/highwater2048_live16/unregistered | 8 | 0.002124 | 0.002148 | yes | unavailable |
| run07-all | registration/live16/late_duplicate | 8 | 0.002307 | 0.002319 | yes | unavailable |
| run07-all | registration/live16/unregistered | 8 | 0.002100 | 0.002100 | yes | unavailable |
| run07-all | registration/live32/late_duplicate | 8 | 0.002209 | 0.002319 | yes | unavailable |
| run07-all | registration/live32/unregistered | 8 | 0.002075 | 0.002100 | yes | unavailable |
| run07-all | registration/live512/late_duplicate | 8 | 0.002307 | 0.002319 | yes | unavailable |
| run07-all | registration/live512/unregistered | 8 | 0.002100 | 0.002115 | yes | unavailable |
| run08-mass | physics/box/adopt_colliders | 64 | 2.700000 | 3.100000 | no | unavailable |
| run08-mass | physics/box/box_mass_divide | 8 | 3.798486 | 3.940903 | yes | unavailable |
| run08-mass | physics/box/prepare_colliders | 64 | 4.800000 | 5.970000 | no | unavailable |
| run08-mass | physics/box/provisional_build | 64 | 37.000000 | 47.415000 | yes | unavailable |
| run08-mass | physics/box/shape_pair_create_dispose | 8 | 1.738281 | 1.847383 | no | unavailable |
| run08-mass | physics/character/adopt_colliders | 64 | 10.600000 | 11.385000 | no | unavailable |
| run08-mass | physics/character/box_mass_divide | 8 | 4.330371 | 4.423374 | yes | unavailable |
| run08-mass | physics/character/prepare_colliders | 64 | 20.100000 | 22.100000 | no | unavailable |
| run08-mass | physics/character/provisional_build | 64 | 73.750000 | 88.635000 | yes | unavailable |
| run08-mass | physics/character/shape_pair_create_dispose | 8 | 6.107031 | 6.700352 | no | unavailable |
| run08-mass | registration/highwater2048_live16/late_duplicate | 8 | 2.010547 | 2.107360 | no | unavailable |
| run08-mass | registration/highwater2048_live16/unregistered | 8 | 2.092285 | 2.203962 | no | unavailable |
| run08-mass | registration/live16/late_duplicate | 8 | 0.022009 | 0.022070 | no | unavailable |
| run08-mass | registration/live16/unregistered | 8 | 0.022180 | 0.030103 | no | unavailable |
| run08-mass | registration/live32/late_duplicate | 8 | 0.051746 | 0.052173 | no | unavailable |
| run08-mass | registration/live32/unregistered | 8 | 0.051807 | 0.053542 | no | unavailable |
| run08-mass | registration/live512/late_duplicate | 8 | 0.744873 | 0.775406 | no | unavailable |
| run08-mass | registration/live512/unregistered | 8 | 0.737769 | 0.773132 | no | unavailable |
| run09-colliders | physics/box/adopt_colliders | 64 | 2.700000 | 2.800000 | yes | unavailable |
| run09-colliders | physics/box/box_mass_divide | 8 | 3.933447 | 4.117246 | no | unavailable |
| run09-colliders | physics/box/prepare_colliders | 64 | 4.500000 | 5.585000 | yes | unavailable |
| run09-colliders | physics/box/provisional_build | 64 | 35.650000 | 39.455000 | no | unavailable |
| run09-colliders | physics/box/shape_pair_create_dispose | 8 | 1.786133 | 1.825645 | no | unavailable |
| run09-colliders | physics/character/adopt_colliders | 64 | 10.400000 | 10.885000 | yes | unavailable |
| run09-colliders | physics/character/box_mass_divide | 8 | 4.073047 | 4.138892 | no | unavailable |
| run09-colliders | physics/character/prepare_colliders | 64 | 19.150000 | 21.095000 | yes | unavailable |
| run09-colliders | physics/character/provisional_build | 64 | 74.250000 | 92.170000 | no | unavailable |
| run09-colliders | physics/character/shape_pair_create_dispose | 8 | 5.761133 | 5.872969 | no | unavailable |
| run09-colliders | registration/highwater2048_live16/late_duplicate | 8 | 1.987622 | 2.091002 | no | unavailable |
| run09-colliders | registration/highwater2048_live16/unregistered | 8 | 1.882434 | 2.000179 | no | unavailable |
| run09-colliders | registration/live16/late_duplicate | 8 | 0.020398 | 0.021536 | no | unavailable |
| run09-colliders | registration/live16/unregistered | 8 | 0.021216 | 0.028549 | no | unavailable |
| run09-colliders | registration/live32/late_duplicate | 8 | 0.048657 | 0.057305 | no | unavailable |
| run09-colliders | registration/live32/unregistered | 8 | 0.047754 | 0.048151 | no | unavailable |
| run09-colliders | registration/live512/late_duplicate | 8 | 0.661304 | 0.680521 | no | unavailable |
| run09-colliders | registration/live512/unregistered | 8 | 0.651758 | 0.670293 | no | unavailable |
| run10-view | physics/box/adopt_colliders | 64 | 2.900000 | 3.085000 | no | unavailable |
| run10-view | physics/box/box_mass_divide | 8 | 4.291260 | 4.838994 | no | unavailable |
| run10-view | physics/box/prepare_colliders | 64 | 4.900000 | 5.800000 | no | unavailable |
| run10-view | physics/box/provisional_build | 64 | 38.300000 | 41.480000 | yes | unavailable |
| run10-view | physics/box/shape_pair_create_dispose | 8 | 1.268164 | 1.334258 | yes | unavailable |
| run10-view | physics/character/adopt_colliders | 64 | 13.200000 | 13.785000 | no | unavailable |
| run10-view | physics/character/box_mass_divide | 8 | 4.861914 | 5.614014 | no | unavailable |
| run10-view | physics/character/prepare_colliders | 64 | 23.550000 | 25.825000 | no | unavailable |
| run10-view | physics/character/provisional_build | 64 | 87.250000 | 94.365000 | yes | unavailable |
| run10-view | physics/character/shape_pair_create_dispose | 8 | 4.390234 | 6.672637 | yes | unavailable |
| run10-view | registration/highwater2048_live16/late_duplicate | 8 | 2.232947 | 2.533267 | no | unavailable |
| run10-view | registration/highwater2048_live16/unregistered | 8 | 2.380615 | 2.422106 | no | unavailable |
| run10-view | registration/live16/late_duplicate | 8 | 0.025708 | 0.025724 | no | unavailable |
| run10-view | registration/live16/unregistered | 8 | 0.025879 | 0.025911 | no | unavailable |
| run10-view | registration/live32/late_duplicate | 8 | 0.060876 | 0.063119 | no | unavailable |
| run10-view | registration/live32/unregistered | 8 | 0.060791 | 0.061178 | no | unavailable |
| run10-view | registration/live512/late_duplicate | 8 | 0.861060 | 0.994094 | no | unavailable |
| run10-view | registration/live512/unregistered | 8 | 0.851160 | 0.932257 | no | unavailable |
| run11-o1 | physics/box/adopt_colliders | 64 | 3.100000 | 3.385000 | no | unavailable |
| run11-o1 | physics/box/box_mass_divide | 8 | 4.616650 | 4.958154 | no | unavailable |
| run11-o1 | physics/box/prepare_colliders | 64 | 5.300000 | 6.485000 | no | unavailable |
| run11-o1 | physics/box/provisional_build | 64 | 41.300000 | 45.225000 | no | unavailable |
| run11-o1 | physics/box/shape_pair_create_dispose | 8 | 2.480273 | 2.584590 | no | unavailable |
| run11-o1 | physics/character/adopt_colliders | 64 | 11.250000 | 12.055000 | no | unavailable |
| run11-o1 | physics/character/box_mass_divide | 8 | 4.746484 | 7.132085 | no | unavailable |
| run11-o1 | physics/character/prepare_colliders | 64 | 20.100000 | 27.150000 | no | unavailable |
| run11-o1 | physics/character/provisional_build | 64 | 76.950000 | 98.110000 | no | unavailable |
| run11-o1 | physics/character/shape_pair_create_dispose | 8 | 5.551172 | 5.659473 | no | unavailable |
| run11-o1 | registration/highwater2048_live16/late_duplicate | 8 | 0.002100 | 0.002115 | yes | unavailable |
| run11-o1 | registration/highwater2048_live16/unregistered | 8 | 0.001904 | 0.001904 | yes | unavailable |
| run11-o1 | registration/live16/late_duplicate | 8 | 0.002063 | 0.002075 | yes | unavailable |
| run11-o1 | registration/live16/unregistered | 8 | 0.001855 | 0.001896 | yes | unavailable |
| run11-o1 | registration/live32/late_duplicate | 8 | 0.002051 | 0.002067 | yes | unavailable |
| run11-o1 | registration/live32/unregistered | 8 | 0.001843 | 0.001855 | yes | unavailable |
| run11-o1 | registration/live512/late_duplicate | 8 | 0.002063 | 0.002075 | yes | unavailable |
| run11-o1 | registration/live512/unregistered | 8 | 0.001855 | 0.001871 | yes | unavailable |
| run12-baseline | physics/box/adopt_colliders | 64 | 3.200000 | 3.700000 | yes | unavailable |
| run12-baseline | physics/box/box_mass_divide | 8 | 4.419141 | 4.511206 | yes | unavailable |
| run12-baseline | physics/box/prepare_colliders | 64 | 5.500000 | 7.055000 | yes | unavailable |
| run12-baseline | physics/box/provisional_build | 64 | 44.150000 | 51.745000 | yes | unavailable |
| run12-baseline | physics/box/shape_pair_create_dispose | 8 | 2.089648 | 2.216367 | yes | unavailable |
| run12-baseline | physics/character/adopt_colliders | 64 | 15.400000 | 16.085000 | yes | unavailable |
| run12-baseline | physics/character/box_mass_divide | 8 | 4.618945 | 4.730171 | yes | unavailable |
| run12-baseline | physics/character/prepare_colliders | 64 | 28.000000 | 30.580000 | yes | unavailable |
| run12-baseline | physics/character/provisional_build | 64 | 106.900000 | 112.535000 | yes | unavailable |
| run12-baseline | physics/character/shape_pair_create_dispose | 8 | 6.471484 | 7.294531 | yes | unavailable |
| run12-baseline | registration/highwater2048_live16/late_duplicate | 8 | 2.044983 | 2.118346 | yes | unavailable |
| run12-baseline | registration/highwater2048_live16/unregistered | 8 | 2.077625 | 2.107808 | yes | unavailable |
| run12-baseline | registration/live16/late_duplicate | 8 | 0.030859 | 0.030875 | yes | unavailable |
| run12-baseline | registration/live16/unregistered | 8 | 0.060437 | 0.062743 | yes | unavailable |
| run12-baseline | registration/live32/late_duplicate | 8 | 0.077429 | 0.079349 | yes | unavailable |
| run12-baseline | registration/live32/unregistered | 8 | 0.068518 | 0.073613 | yes | unavailable |
| run12-baseline | registration/live512/late_duplicate | 8 | 0.809058 | 0.856812 | yes | unavailable |
| run12-baseline | registration/live512/unregistered | 8 | 0.938977 | 0.966445 | yes | unavailable |
