// @ts-nocheck
/* eslint-disable */
/* tslint:disable */
/* prettier-ignore-start */
import React from "react";
import { classNames } from "@plasmicapp/react-web";

export type InnoM3DIconProps = React.ComponentProps<"svg"> & {
  title?: string;
};

export function InnoM3DIcon(props: InnoM3DIconProps) {
  const { className, style, title, ...restProps } = props;
  return (
    <svg
      xmlns={"http://www.w3.org/2000/svg"}
      fill={"none"}
      viewBox={"0 0 71 74"}
      height={"1em"}
      width={"1em"}
      className={classNames("plasmic-default__svg", className)}
      style={style}
      {...restProps}
    >
      {title && <title>{title}</title>}

      <g clipPath={"url(#JhfhfRnaQJa)"} fill={"currentColor"}>
        <path
          d={
            "M34.915 0L0 74 39.482 9.5 34.915 0zm4.857 10.101L7.535 73.977l35.76-56.659-3.523-7.217zm3.836 7.855L13.47 73.965l34.056-47.971-3.918-8.04zm5.796 11.874L36.375 47.205 50.564 73.93 71 74 49.404 29.83z"
          }
        ></path>
      </g>

      <defs>
        <clipPath id={"JhfhfRnaQJa"}>
          <path fill={"currentColor"} d={"M0 0h71v74H0z"}></path>
        </clipPath>
      </defs>
    </svg>
  );
}

export default InnoM3DIcon;
/* prettier-ignore-end */
