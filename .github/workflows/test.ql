import csharp

from ObjectCreation oc, Class c
where 
    oc.getType() = c and
    c.getName() = "InvoiceLineType" and
    c.getNamespace().getName() = "FacturaElectronicaV321"
select oc, "New instance of InvoiceLineType created."

