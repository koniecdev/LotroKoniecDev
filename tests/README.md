## Windows Smart App Control

### Polski

Jeśli masz włączoną funkcję **Inteligentna kontrola aplikacji** (Smart App Control) w systemie Windows, może ona blokować uruchamianie lokalnie skompilowanych plików `.dll`, ponieważ nie posiadają one zaufanego podpisu cyfrowego (Authenticode). Objawia się to błędem `System.IO.FileLoadException` z kodem `0x800711C7` podczas uruchamiania testów lub aplikacji.

**Rozwiązanie — przełącz SAC w tryb ewaluacji:**

1. Otwórz **Zabezpieczenia Windows**
2. Przejdź do **Kontrola aplikacji i przeglądarki**
3. Kliknij **Ustawienia funkcji Inteligentna kontrola aplikacji**
4. Wybierz **Ewaluacja**

Tryb ewaluacji pozwala systemowi uczyć się wzorców użytkowania i nie blokuje lokalnych buildów. Nie wymaga permanentnego wyłączenia ochrony.

Jeśli tryb ewaluacji nie jest dostępny (widoczne są tylko opcje **Włączone** / **Wyłączone**), oznacza to, że SAC opuścił już fazę ewaluacji. W takim przypadku jedyną opcją jest wyłączenie SAC. Uwaga: po wyłączeniu nie można go ponownie włączyć bez reinstalacji systemu.

---

### English

If you have **Smart App Control** enabled on Windows, it may block locally compiled `.dll` files from running because they lack a trusted digital signature (Authenticode). This manifests as a `System.IO.FileLoadException` with error code `0x800711C7` when running tests or the application.

**Solution — switch SAC to evaluation mode:**

1. Open **Windows Security**
2. Go to **App & browser control**
3. Click **Smart App Control settings**
4. Select **Evaluation**

Evaluation mode allows the system to learn your usage patterns and won't block local builds. It does not require permanently disabling protection.

If evaluation mode is not available (only **On** / **Off** options are shown), it means SAC has already left the evaluation phase. In that case, the only option is to turn SAC off. Note: once disabled, it cannot be re-enabled without reinstalling Windows.
