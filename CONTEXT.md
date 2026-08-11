# FieldOps Portal

FieldOps Portal models customer, sales, and field-work operations for a multi-branch service company. This glossary fixes the business language used by the application, documentation, and tests.

## Parties

**Party**:
A person or organization with whom the company has a business relationship. A party may hold more than one role.
_Avoid_: Account, company record, master record

**Customer**:
A party that receives inspection, construction, or maintenance services.
_Avoid_: Client, buyer

**Business Partner**:
A party that supports delivery as a supplier, subcontractor, or referral source.
_Avoid_: Vendor record, outside company

**Contact**:
A named person through whom the company communicates with an organizational party.
_Avoid_: Customer, user

**Site**:
A physical location at which inspection, construction, or maintenance work is performed.
_Avoid_: Customer address, branch

**Party Branch Assignment**:
The relationship that authorizes a branch to service and maintain a party. A party may be assigned to more than one branch.
_Avoid_: Party ownership, customer branch

## Sales and Work

**Sales Opportunity**:
A potential engagement tracked from initial inquiry through award, loss, or hold.
_Avoid_: Work order, construction record, lead record

**Work Order**:
An awarded unit of inspection, construction, or maintenance work scheduled for a site.
_Avoid_: Sales opportunity, work history

**Work Event**:
A dated record of a visit, action, observation, or completion activity performed for a work order.
_Avoid_: Work order, free-form note

## Organization and Control

**Branch**:
An operating office that owns users, opportunities, and work orders.
_Avoid_: Site, department

**Application User**:
An employee-shaped demo identity that signs in to FieldOps Portal and acts within an assigned role and branch.
_Avoid_: Customer, contact

**Audit Entry**:
An immutable business record describing who changed which domain record, when, and with what outcome.
_Avoid_: Debug log, request log

**Demo Reset**:
An administrator-only operation that atomically replaces all mutable demo data with the approved seed dataset.
_Avoid_: Database migration, automatic reset
