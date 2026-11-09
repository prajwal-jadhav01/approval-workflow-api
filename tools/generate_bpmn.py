"""Generates bpmn/expense-claim-approval.bpmn, including diagram layout for Camunda Modeler.

Kept as a script so the process and its layout are reviewable in a diff. Run: python3 tools/generate_bpmn.py
"""
from pathlib import Path
from xml.sax.saxutils import escape

# id: (kind, name, x, y, extra)
N = {
    "start": ("startEvent", "Claim submitted", 60, 200, {}),
    "validate": ("serviceTask", "Validate claim", 140, 178, {"type": "claim.validate"}),
    "gwValid": ("exclusiveGateway", "Valid?", 300, 193, {}),
    "reqManager": ("serviceTask", "Request manager approval", 390, 178, {"type": "claim.request-manager-approval"}),
    "waitManager": ("intermediateCatchEvent", "Manager decision", 552, 200, {"message": "msgManager"}),
    "gwManager": ("exclusiveGateway", "Manager approved?", 630, 193, {}),
    "gwAmount": ("exclusiveGateway", "Amount over 1000?", 730, 193, {}),
    "reqFinance": ("serviceTask", "Request finance approval", 830, 178, {"type": "claim.request-finance-approval"}),
    "waitFinance": ("intermediateCatchEvent", "Finance decision", 992, 200, {"message": "msgFinance"}),
    "gwFinance": ("exclusiveGateway", "Finance approved?", 1070, 193, {}),
    "gwJoin": ("exclusiveGateway", "", 1170, 193, {}),
    "approve": ("serviceTask", "Mark approved", 1260, 178, {"type": "claim.approve"}),
    "endApproved": ("endEvent", "Claim approved", 1422, 200, {}),
    "reject": ("serviceTask", "Mark rejected", 830, 340, {"type": "claim.reject"}),
    "endRejected": ("endEvent", "Claim rejected", 862, 470, {}),
}
SIZES = {"startEvent": (36, 36), "endEvent": (36, 36), "intermediateCatchEvent": (36, 36),
         "serviceTask": (100, 80), "exclusiveGateway": (50, 50)}

# (id, source, target, name, condition)
F = [
    ("f1", "start", "validate", "", None),
    ("f2", "validate", "gwValid", "", None),
    ("fValidYes", "gwValid", "reqManager", "yes", "=valid"),
    ("fValidNo", "gwValid", "reject", "no", "=not(valid)"),
    ("f3", "reqManager", "waitManager", "", None),
    ("f4", "waitManager", "gwManager", "", None),
    ("fMgrYes", "gwManager", "gwAmount", "yes", "=managerApproved"),
    ("fMgrNo", "gwManager", "reject", "no", "=not(managerApproved)"),
    ("fBig", "gwAmount", "reqFinance", "yes", "=amount > 1000"),
    ("fSmall", "gwAmount", "gwJoin", "no", "=amount <= 1000"),
    ("f5", "reqFinance", "waitFinance", "", None),
    ("f6", "waitFinance", "gwFinance", "", None),
    ("fFinYes", "gwFinance", "gwJoin", "yes", "=financeApproved"),
    ("fFinNo", "gwFinance", "reject", "no", "=not(financeApproved)"),
    ("f7", "gwJoin", "approve", "", None),
    ("f8", "approve", "endApproved", "", None),
    ("f9", "reject", "endRejected", "", None),
]

# Event subprocess: cancellation can arrive at any point and interrupts the main flow.
SUB = {"x": 390, "y": 460, "w": 360, "h": 160}
SUB_NODES = {
    "cancelStart": ("startEvent", "Cancel requested", 430, 522, {"message": "msgCancel", "interrupting": True}),
    "cancelTask": ("serviceTask", "Mark cancelled", 510, 500, {"type": "claim.cancel"}),
    "cancelEnd": ("endEvent", "Claim cancelled", 672, 522, {}),
}
SUB_FLOWS = [("c1", "cancelStart", "cancelTask", "", None), ("c2", "cancelTask", "cancelEnd", "", None)]

MESSAGES = {"msgManager": "manager-decision", "msgFinance": "finance-decision", "msgCancel": "cancel-claim"}


def bounds(n):
    kind, _, x, y, _ = n
    w, h = SIZES[kind]
    return x, y, w, h


def center(n):
    x, y, w, h = bounds(n)
    return x + w / 2, y + h / 2


def node_xml(nid, n, flows, indent):
    kind, name, *_, extra = n
    ins = [f for f in flows if f[2] == nid]
    outs = [f for f in flows if f[1] == nid]
    attrs = f'id="{nid}" name="{name}"'
    if kind == "startEvent" and "interrupting" in extra:
        attrs += ' isInterrupting="true"'
    lines = [f"{indent}<bpmn:{kind} {attrs}>"]
    if kind == "serviceTask":
        lines += [f"{indent}  <bpmn:extensionElements>",
                  f'{indent}    <zeebe:taskDefinition type="{extra["type"]}" retries="3" />',
                  f"{indent}  </bpmn:extensionElements>"]
    lines += [f"{indent}  <bpmn:incoming>{f[0]}</bpmn:incoming>" for f in ins]
    lines += [f"{indent}  <bpmn:outgoing>{f[0]}</bpmn:outgoing>" for f in outs]
    if "message" in extra:
        lines.append(f'{indent}  <bpmn:messageEventDefinition id="{nid}Def" messageRef="{extra["message"]}" />')
    lines.append(f"{indent}</bpmn:{kind}>")
    return "\n".join(lines)


def flow_xml(f, indent):
    fid, s, t, name, cond = f
    if cond is None:
        return f'{indent}<bpmn:sequenceFlow id="{fid}" name="{name}" sourceRef="{s}" targetRef="{t}" />'
    return (f'{indent}<bpmn:sequenceFlow id="{fid}" name="{name}" sourceRef="{s}" targetRef="{t}">\n'
            f'{indent}  <bpmn:conditionExpression xsi:type="bpmn:tFormalExpression">{escape(cond)}</bpmn:conditionExpression>\n'
            f"{indent}</bpmn:sequenceFlow>")


# Flows that need a hand-drawn route instead of the default one.
ROUTES = {
    # "Amount over 1000? no" jumps over the finance steps along the top.
    "fSmall": lambda s, t: [(center(s)[0], bounds(s)[1]), (center(s)[0], 120), (center(t)[0], 120),
                            (center(t)[0], bounds(t)[1])],
}


def edge_points(s, t):
    (sx, sy), (tx, ty) = center(s), center(t)
    _, _, sw, sh = bounds(s)
    _, _, tw, th = bounds(t)
    if abs(sy - ty) < 1:  # same row: right edge to left edge
        return [(sx + sw / 2, sy), (tx - tw / 2, ty)]
    if ty > sy:  # going down to the reject row: leave from the bottom, enter from the top or side
        if abs(sx - tx) < 1:
            return [(sx, sy + sh / 2), (tx, ty - th / 2)]
        if tx > sx:
            return [(sx, sy + sh / 2), (sx, ty), (tx - tw / 2, ty)]
        return [(sx, sy + sh / 2), (sx, ty), (tx + tw / 2, ty)]
    return [(sx, sy - sh / 2), (sx, ty), (tx - tw / 2, ty)]


def shape_xml(nid, n):
    x, y, w, h = bounds(n)
    extra = ' isMarkerVisible="true"' if n[0] == "exclusiveGateway" else ""
    label = ""
    if n[0] != "serviceTask" and n[1]:
        # Gateway labels go above the diamond: the "no" branches leave from the bottom.
        label_y = y - 32 if n[0] == "exclusiveGateway" else y + h + 5
        label = (f'\n        <bpmndi:BPMNLabel><dc:Bounds x="{x - 20}" y="{label_y}" '
                 f'width="{w + 40}" height="27" /></bpmndi:BPMNLabel>')
    return (f'      <bpmndi:BPMNShape id="{nid}_di" bpmnElement="{nid}"{extra}>\n'
            f'        <dc:Bounds x="{x}" y="{y}" width="{w}" height="{h}" />{label}\n'
            f"      </bpmndi:BPMNShape>")


def edge_xml(f, nodes):
    route = ROUTES.get(f[0])
    pts = route(nodes[f[1]], nodes[f[2]]) if route else edge_points(nodes[f[1]], nodes[f[2]])
    wps = "\n".join(f'        <di:waypoint x="{x:.0f}" y="{y:.0f}" />' for x, y in pts)
    return f'      <bpmndi:BPMNEdge id="{f[0]}_di" bpmnElement="{f[0]}">\n{wps}\n      </bpmndi:BPMNEdge>'


def main():
    msgs = "\n".join(
        f'  <bpmn:message id="{mid}" name="{name}">\n'
        f'    <bpmn:extensionElements><zeebe:subscription correlationKey="=claimId" /></bpmn:extensionElements>\n'
        f"  </bpmn:message>" for mid, name in MESSAGES.items())
    body = "\n".join([node_xml(i, n, F, "    ") for i, n in N.items()] + [flow_xml(f, "    ") for f in F])
    sub = "\n".join([node_xml(i, n, SUB_FLOWS, "      ") for i, n in SUB_NODES.items()]
                    + [flow_xml(f, "      ") for f in SUB_FLOWS])
    all_nodes = {**N, **SUB_NODES}
    shapes = "\n".join([shape_xml(i, n) for i, n in all_nodes.items()])
    edges = "\n".join([edge_xml(f, all_nodes) for f in F + SUB_FLOWS])
    xml = f'''<?xml version="1.0" encoding="UTF-8"?>
<!-- Generated by tools/generate_bpmn.py. Edit the script, not this file. -->
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                  xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                  xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                  xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                  xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"
                  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                  xmlns:modeler="http://camunda.org/schema/modeler/1.0"
                  id="claims-definitions" targetNamespace="http://bpmn.io/schema/bpmn"
                  modeler:executionPlatform="Camunda Cloud" modeler:executionPlatformVersion="8.6.0">
  <bpmn:process id="expense-claim-approval" name="Expense claim approval" isExecutable="true">
{body}
    <bpmn:subProcess id="cancellation" name="Cancellation" triggeredByEvent="true">
{sub}
    </bpmn:subProcess>
  </bpmn:process>
{msgs}
  <bpmndi:BPMNDiagram id="diagram">
    <bpmndi:BPMNPlane id="plane" bpmnElement="expense-claim-approval">
      <bpmndi:BPMNShape id="cancellation_di" bpmnElement="cancellation" isExpanded="true">
        <dc:Bounds x="{SUB["x"]}" y="{SUB["y"]}" width="{SUB["w"]}" height="{SUB["h"]}" />
      </bpmndi:BPMNShape>
{shapes}
{edges}
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</bpmn:definitions>
'''
    out = Path(__file__).resolve().parent.parent / "bpmn" / "expense-claim-approval.bpmn"
    out.write_text(xml)
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
