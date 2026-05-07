/*
 * MVCWebView Index page glue. Generic SVG mechanics live in RoslynGraphUi
 * (loaded from RoslynGraphUi.Shared via _GraphPageHead.cshtml). This file
 * carries only the bits that are specific to the dependency-analysis UI:
 *   - three view modes (single class, all controllers, entire project)
 *   - the classInput format "ClassName : InterfaceName"
 *   - the text-report panels on the right rail
 */

const ViewType = {
    CONTROLLER: "Controller",
    ENTIRE_PROJECT: "EntireProject",
    SINGLE_CLASS: "SingleClass"
};

let entireProject = false;
let allControllers = false;

document.addEventListener("DOMContentLoaded", () => {
    RoslynGraphUi.init({
        svgContainerId: "svgOutput",
        loadingOverlayId: "loadingOverlay",
        onNodeClick: (className) => {
            const input = document.getElementById("classInput");
            input.value = className;
            input.dispatchEvent(new Event("input", { bubbles: true }));
            loadSingleNode();
        },
        onBack: (previousView) => {
            if (!previousView) return;
            if (previousView.project) {
                document.getElementById("projectInput").value = previousView.project;
            }
            switch (previousView.type) {
                case ViewType.CONTROLLER:
                    loadAllControllers();
                    break;
                case ViewType.ENTIRE_PROJECT:
                    loadEntireProject();
                    break;
                case ViewType.SINGLE_CLASS:
                    if (previousView.className) {
                        document.getElementById("classInput").value = previousView.className;
                    }
                    loadSingleNode();
                    break;
            }
        },
    });
});

async function loadSingleNode() {
    allControllers = false;
    entireProject = false;

    const className = document.getElementById("classInput").value;
    if (!className) return window.alert("No class selected");

    loadTextReports();
    await loadSingleClassSvg();
}

async function loadEntireProject() {
    allControllers = false;
    entireProject = true;
    RoslynGraphUi.showLoading();
    loadTextReports();
    const project = document.getElementById("projectInput").value;

    const response = await fetch(`/api/SVG/GetEntireProjectSVG?project=${encodeURIComponent(project)}`);
    if (response.ok) {
        const svg = await response.text();
        RoslynGraphUi.renderSvg(svg, {
            contextToPush: { type: ViewType.ENTIRE_PROJECT, project },
        });
    } else {
        document.getElementById("svgOutput").innerText = "Failed to load SVG.";
    }
    RoslynGraphUi.hideLoading();
}

async function loadAllControllers() {
    allControllers = true;
    entireProject = false;
    RoslynGraphUi.showLoading();
    loadTextReports();
    const project = document.getElementById("projectInput").value;

    const response = await fetch(`/api/SVG/GetAllControllersSVG?project=${encodeURIComponent(project)}`);
    if (response.ok) {
        const svg = await response.text();
        RoslynGraphUi.renderSvg(svg, {
            contextToPush: { type: ViewType.CONTROLLER, project },
        });
    } else {
        document.getElementById("svgOutput").innerText = "Failed to load SVG.";
    }
    RoslynGraphUi.hideLoading();
}

async function loadSingleClassSvg() {
    RoslynGraphUi.showLoading();
    const rawClassName = document.getElementById("classInput").value;
    const parts = rawClassName.split(":").map(s => s.trim());
    const className = parts[0] || "";
    const interfaceName = parts[1] || "";
    const project = document.getElementById("projectInput").value;

    const response = await fetch(
        `/api/SVG/GetSvg?implementationName=${encodeURIComponent(className)}` +
        `&interfaceName=${encodeURIComponent(interfaceName)}` +
        `&project=${encodeURIComponent(project)}`
    );
    if (response.ok) {
        const svg = await response.text();
        RoslynGraphUi.renderSvg(svg, {
            contextToPush: { type: ViewType.SINGLE_CLASS, project, className },
            centerOnNodeId: className,
        });
    } else {
        document.getElementById("svgOutput").innerText = "Failed to load SVG.";
    }
    RoslynGraphUi.hideLoading();
}

function loadPrevious() {
    RoslynGraphUi.back();
}

async function loadTextReports() {
    fetchTextReport("Tree", "output-lifetime-violations");
    fetchTextReport("Cycles", "output-cycles");
    fetchTextReport("StatefulServices", "output-stateful-report");
    fetchTextReport("ExcessiveDependencies", "output-excessive-deps");
    fetchTextReport("ManualLifecycleManagement", "output-manual-lifestyle");
    fetchTextReport("UnusedMethods", "output-unused-methods");
    fetchTextReport("ManualInstantiation", "output-manual-instantiation");
    fetchTextReport("CaptiveDependencies", "output-captive-dependencies");
    fetchTextReport("TransientManualResolutions", "output-transient-manual-resolutions");
}

async function fetchTextReport(endpoint, outputId) {
    const rawClassName = document.getElementById("classInput").value;
    const parts = rawClassName.split(":").map(s => s.trim());
    const className = parts[0] || "";
    const interfaceName = parts[1] || "";
    const project = document.getElementById("projectInput").value;

    const params =
        `type=${encodeURIComponent(endpoint)}` +
        `&implementationName=${encodeURIComponent(className)}` +
        `&interfaceName=${encodeURIComponent(interfaceName)}` +
        `&project=${encodeURIComponent(project)}` +
        `&entireProject=${encodeURIComponent(entireProject)}` +
        `&allControllers=${encodeURIComponent(allControllers)}`;

    const response = await fetch(`/api/TextReport/GetTextReport?${params}`);

    if (!response.ok) {
        const errorText = await response.text();
        document.getElementById(outputId).textContent = "Error: " + errorText;
        return;
    }

    const html = await response.text();
    document.getElementById(outputId).innerHTML = html;
}
