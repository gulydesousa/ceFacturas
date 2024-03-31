using System;
using System.Xml.Serialization;
using BO.Facturacion;
using static BO.Sistema.EFormatoImpresion.Facturacion;

namespace BL.Facturacion
{
    public static partial class cEFacturasBL
    {
        /// <summary>
        /// Creates an invoice line for a measured service.
        /// </summary>
        /// <param name="factura">The factura object.</param>
        /// <param name="tarifa">The tarifa object.</param>
        /// <param name="lineaFactura">The lineaFactura object.</param>
        /// <param name="c">The index of the array.</param>
        /// <returns>The created InvoiceLineType object.</returns>
        private static 
        FacturaElectronicaV321.InvoiceLineType CreateInvoiceLineForServiceMeasured(
              cFacturaBO factura
            , cTarifaBO tarifa
            , cLineaFacturaBO lineaFactura
            , int c)
        {
            FacturaElectronicaV321.InvoiceLineType oInvoiceLine = new FacturaElectronicaV321.InvoiceLineType()
            {
                IssuerContractReference = factura.ContratoCodigo.ToString(),
                IssuerTransactionDate = factura.Fecha.Value,
                ReceiverContractReference = factura.ContratoCodigo.ToString(),
                ArticleCode = tarifa.CodigoServicio.ToString() + "-" + tarifa.Codigo.ToString() + " - Consumo",
                ItemDescription = "Consumo hasta " + lineaFactura.ArrayEscalas[c].ToString(),
                Quantity = Convert.ToDouble(lineaFactura.ArrayUnidades[c]),
                UnitOfMeasure = FacturaElectronicaV321.UnitOfMeasureType.Item33,
                UnitOfMeasureSpecified = true,
                UnitPriceWithoutTax = Convert.ToDouble(lineaFactura.ArrayPrecios[c]),
                TotalCost = Convert.ToDouble((lineaFactura.ArrayPrecios[c] * lineaFactura.ArrayUnidades[c]).ToString("N2")),
                GrossAmount = Convert.ToDouble((lineaFactura.ArrayPrecios[c] * lineaFactura.ArrayUnidades[c]).ToString("N2"))
            };

            oInvoiceLine.TaxesOutputs = new FacturaElectronicaV321.InvoiceLineTypeTax[1];
            oInvoiceLine.TaxesOutputs[0] = new FacturaElectronicaV321.InvoiceLineTypeTax();
            oInvoiceLine.TaxesOutputs[0].TaxRate = Convert.ToDouble(lineaFactura.PtjImpuesto);
            oInvoiceLine.TaxesOutputs[0].TaxableBase = new FacturaElectronicaV321.AmountType();
            oInvoiceLine.TaxesOutputs[0].TaxableBase.TotalAmount = Convert.ToDouble((lineaFactura.ArrayPrecios[c] * lineaFactura.ArrayUnidades[c]).ToString("N2"));

            return oInvoiceLine;
        }

        /// <summary>
        /// Retrieves the enum value based on the specified string value.
        /// </summary>
        /// <typeparam name="T">The enum type.</typeparam>
        /// <param name="value">The string value.</param>
        /// <returns>The enum value.</returns>
        private static T GetEnumValue<T>(string value) where T : Enum
        {
            foreach (var enumValue in Enum.GetValues(typeof(T)))
            {
                if (((XmlEnumAttribute)typeof(T).GetMember(enumValue.ToString())[0].GetCustomAttributes(typeof(XmlEnumAttribute), false)[0]).Name == value)
                {
                    return (T)enumValue;
                }
            }
            return default;
        }
    }
}