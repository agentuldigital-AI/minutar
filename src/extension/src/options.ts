const labelInput = document.getElementById("label") as HTMLInputElement;
const saveBtn = document.getElementById("save") as HTMLButtonElement;
const statusSpan = document.getElementById("status") as HTMLSpanElement;

void chrome.storage.local.get(["profileLabel"]).then((st) => {
  labelInput.value = (st.profileLabel as string | undefined) ?? "";
});

saveBtn.addEventListener("click", () => {
  void chrome.storage.local.set({ profileLabel: labelInput.value.trim() }).then(() => {
    statusSpan.textContent = "salvat ✓";
    setTimeout(() => (statusSpan.textContent = ""), 2000);
  });
});
