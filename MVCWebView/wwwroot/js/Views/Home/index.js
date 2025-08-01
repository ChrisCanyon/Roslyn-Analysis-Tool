async function loadSingleNode() {
    allControllers = false;
    entireProject = false;
    const className = document.getElementById("classInput").value;
    if (!className) return window.alert("No class selected");

    loadTextReports();
    loadSingleClassSvg();
} 

document.addEventListener("DOMContentLoaded", () => {
    const input = document.getElementById("classInput");
    console.log("adding listener");
    input.addEventListener("input", (event) => {
        el = event.target;
        const length = Math.max(el.value.length, 25);
        el.style.setProperty("width", `${length}ch`, "important");
    });
});

async function loadPrevious() {
    if (historyStack.length < 2) {
        console.warn("No previous view.");
        return;
    }

    const currentView = historyStack.pop();
    const lastView = historyStack.pop();

    if (!lastView.project) {
        console.warn("Missing class name for SINGLE_CLASS view.");
        return;
    }
    document.getElementById("projectInput").value = lastView.project;

    switch (lastView.type) {
        case ViewType.CONTROLLER:
            await loadAllControllers();
            break;

        case ViewType.ENTIRE_PROJECT:
            await loadEntireProject();
            break;

        case ViewType.SINGLE_CLASS:
            if (!lastView.className) {
                console.warn("Missing class name for SINGLE_CLASS view.");
                return;
            }
            document.getElementById("classInput").value = lastView.className;
            await loadSingleNode(lastView.project, lastView.className);
            break;

        default:
            console.warn("Unknown view type:", lastView.type);
    }
}

const ViewType = {
    CONTROLLER: "Controller",
    ENTIRE_PROJECT: "EntireProject",
    SINGLE_CLASS: "SingleClass"
};

const historyStack = [];

function createViewContext(type, project, className = null) {
    return {
        type,               // one of ViewType
        project,            // string
        className           // string or null (only for SINGLE_CLASS)
    };
}

var entireProject = false;
async function loadEntireProject() {
    allControllers = false;
    entireProject = true;
    showLoading();
    loadTextReports(true, false);
    const project = document.getElementById('projectInput').value;

    const response = await fetch(`/api/SVG/GetEntireProjectSVG?project=${encodeURIComponent(project)}`);
    if (response.ok) {
        const svg = await response.text();
        document.getElementById('svgOutput').innerHTML = svg;
        historyStack.push(createViewContext(ViewType.ENTIRE_PROJECT, project))
    } else {
        document.getElementById('svgOutput').innerText = 'Failed to load SVG.';
    }
    hideLoading();
    configureSVG();
}

var allControllers = false;
async function loadAllControllers() {
    allControllers = true;
    entireProject = false;
    showLoading();
    loadTextReports(false, true);
    const project = document.getElementById('projectInput').value;

    const response = await fetch(`/api/SVG/GetAllControllersSVG?project=${encodeURIComponent(project)}`);
    if (response.ok) {
        const svg = await response.text();
        document.getElementById('svgOutput').innerHTML = svg;
        historyStack.push(createViewContext(ViewType.CONTROLLER, project))
    } else {
        document.getElementById('svgOutput').innerText = 'Failed to load SVG.';
    }
    hideLoading();
    configureSVG();
}

async function loadTextReports() {
    fetchTextReport("Tree", "output-lifetime-violations");
    fetchTextReport("Cycles", "output-cycles");
    fetchTextReport("ExcessiveDependencies", "output-excessive-deps");
    fetchTextReport("ManualLifecycleManagement", "output-manual-lifestyle");
    fetchTextReport("UnusedMethods", "output-unused-methods");
    fetchTextReport("ManualInstantiation", "output-manual-instantiation");
};

async function fetchTextReport(endpoint, outputId){
    const className = document.getElementById("classInput").value;
    const project = document.getElementById("projectInput").value;


    var params = `type=${encodeURIComponent(endpoint)}&className=${encodeURIComponent(className)}&project=${encodeURIComponent(project)}&entireProject=${encodeURIComponent(entireProject)}&allControllers=${encodeURIComponent(allControllers) }`
    const response = await fetch(`/api/TextReport/GetTextReport?${params}`);

    if (!response.ok) {
        const errorText = await response.text();
        document.getElementById(outputId).textContent = "Error: " + errorText;
        return;
    }

    const html = await response.text();
    document.getElementById(outputId).innerHTML = html;
}

async function loadSingleClassSvg() {
    showLoading();
    const className = document.getElementById('classInput').value;
    const project = document.getElementById('projectInput').value;

    const response = await fetch(`/api/SVG/GetSvg?className=${encodeURIComponent(className)}&project=${encodeURIComponent(project)}`);
    if (response.ok) {
        historyStack.push(createViewContext(ViewType.SINGLE_CLASS, project, className))
        const svg = await response.text();
        document.getElementById('svgOutput').innerHTML = svg;
    } else {
        document.getElementById('svgOutput').innerText = 'Failed to load SVG.';
    }
    hideLoading();

    configureSVG(className);
}

function configureSVG(className) {
    const svg = document.querySelector("#svgOutput svg");
    if (svg) {
        svg.removeAttribute("width");
        svg.removeAttribute("height");
        svg.setAttribute("width", "100%");
        svg.setAttribute("height", "100%");
        svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
        svg.style.maxWidth = "100%";
        svg.style.height = "100%";

        // Optional if missing:
        if (!svg.hasAttribute("viewBox")) {
            const bbox = svg.getBBox();
            svg.setAttribute("viewBox", `0 0 ${bbox.width} ${bbox.height}`);
        }

        svgPanZoom(svg, {
            zoomEnabled: true,
            controlIconsEnabled: true,
            fit: true,
            center: true,
            minZoom: 0.5,
            maxZoom: 1000,
            contain: true,
        });

        attachClickHandlers();
        if(className) centerSVGOnSelectedClass(className);
    }
}

function centerSVGOnSelectedClass(className) {
    const node = findNodeByTitle(className);
    if (!node) return;

    const svg = document.querySelector("#svgOutput svg");
    if (!svg || !window.panZoomInstance) return;

    const bbox = node.getBBox();
    const viewCenter = {
        x: svg.clientWidth / 2,
        y: svg.clientHeight / 2
    };

    const zoom = window.panZoomInstance.getZoom();
    window.panZoomInstance.pan({
        x: viewCenter.x - (bbox.x + bbox.width / 2) * zoom,
        y: viewCenter.y - (bbox.y + bbox.height / 2) * zoom
    });
}

function findNodeByTitle(className) {
    const svg = document.querySelector("#svgOutput svg");
    const nodes = svg.querySelectorAll("g.node");

    for (const node of nodes) {
        const title = node.querySelector("title");
        if (title?.textContent.trim() === className) {
            return node;
        }
    }
    return null;
}

function attachClickHandlers() {
    const svg = document.querySelector("#svgOutput svg");
    if (!svg) return;

    const nodes = svg.querySelectorAll('g.node');

    nodes.forEach(node => {
        const titleElement = node.querySelector("title");
        if (!titleElement) return;

        const className = titleElement.textContent?.trim();
        if (!className) return;

        node.style.cursor = "pointer";
        node.addEventListener("click", () => {
            console.log("Clicked node:", className);
            const input = document.getElementById("classInput");
            input.value = className
            input.dispatchEvent(new Event("input", { bubbles: true }));
            loadSingleNode();
        });
    });
}

function showLoading() {
    document.getElementById("loadingOverlay").style.display = "flex";
}

function hideLoading() {
    document.getElementById("loadingOverlay").style.display = "none";
}