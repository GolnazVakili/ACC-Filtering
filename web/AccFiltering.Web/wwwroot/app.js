const form = document.querySelector("#job-form");
const runButton = document.querySelector("#run");
const formError = document.querySelector("#form-error");
const result = document.querySelector("#result");
const resultTitle = document.querySelector("#result-title");
const resultMessage = document.querySelector("#result-message");
const paths = document.querySelector("#paths");
const folders = document.querySelector("#folders");
const summary = document.querySelector("#summary");
const rows = document.querySelector("#rows");
const download = document.querySelector("#download");

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  formError.hidden = true;
  const data = new FormData(form);
  data.set("execute", form.execute.checked ? "true" : "false");
  data.set("overwrite", form.overwrite.checked ? "true" : "false");

  runButton.disabled = true;
  showResult("Running", "Resolving folders and reading the workbook…", true);

  try {
    const response = await fetch("/api/jobs", { method: "POST", body: data });
    const payload = await response.json();
    if (!response.ok) {
      throw new Error(payload.message || "The run could not start.");
    }

    await poll(payload.id);
  } catch (error) {
    showResult("Could not start", error.message, false);
  } finally {
    runButton.disabled = false;
  }
});

async function poll(id) {
  download.hidden = true;
  download.href = `/api/jobs/${id}/report`;
  for (;;) {
    const response = await fetch(`/api/jobs/${id}`);
    if (!response.ok) {
      throw new Error("The job status could not be read.");
    }

    const status = await response.json();
    render(status);
    if (status.state !== "running") {
      if (status.rows.length > 0) {
        download.hidden = false;
      }
      return;
    }

    await delay(1000);
  }
}

function render(status) {
  showResult(status.state === "running" ? "Running" : status.state === "succeeded" ? "Finished" : "Needs attention", status.message, status.state === "running");
  paths.replaceChildren();
  addPath("Source", status.sourcePath);
  addPath("Destination", status.destinationPath);

  folders.hidden = status.availableFolders.length === 0;
  folders.replaceChildren();
  for (const name of status.availableFolders) {
    const item = document.createElement("li");
    item.textContent = name;
    folders.appendChild(item);
  }

  summary.replaceChildren();
  const counts = status.summary;
  for (const [label, value] of [
    ["Excel", status.excelCount],
    ["Matched", counts.matched],
    ["Copied", counts.copied],
    ["Skipped", counts.skipped],
    ["Not found", counts.notFound],
    ["Dry run", counts.dryRun],
    ["Failed", counts.failed]
  ]) {
    if (!value) {
      continue;
    }
    const pill = document.createElement("span");
    pill.className = "pill";
    pill.textContent = `${label}: ${value}`;
    summary.appendChild(pill);
  }

  rows.replaceChildren();
  for (const row of status.rows) {
    const tr = document.createElement("tr");
    for (const value of [row.excelName, row.matchedName, row.status, row.error]) {
      const cell = document.createElement("td");
      cell.textContent = value || "";
      tr.appendChild(cell);
    }
    rows.appendChild(tr);
  }
}

function addPath(label, value) {
  if (!value) {
    return;
  }
  const term = document.createElement("dt");
  term.textContent = label;
  const detail = document.createElement("dd");
  detail.textContent = value;
  paths.append(term, detail);
}

function showResult(title, message, busy) {
  result.hidden = false;
  resultTitle.textContent = title;
  resultMessage.textContent = message;
  runButton.textContent = busy ? "Running…" : "Run";
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
